// -----------------------------------------------------------------------------
//  Copyright (c) 2024-present NoMercy Entertainment. All rights reserved.
//
//  This file is part of NoMercy MediaServer, source-available software (NOT open
//  source). Personal use and contributions are welcome; distribution, resale,
//  relicensing, and commercial exploitation are prohibited without explicit
//  written consent. See LICENSE for full terms. Distributed WITHOUT ANY WARRANTY.
//
//  SPDX-License-Identifier: LicenseRef-NoMercy-Proprietary
// -----------------------------------------------------------------------------

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NoMercy.NmSystem.Configuration;
using NoMercy.NmSystem.Monitoring;
using NoMercy.Queue.MediaServer.Configuration;
using NoMercy.Resources;
using NoMercyQueue;
using NoMercyQueue.Core.Interfaces;
using NoMercyQueue.Core.Models;
using NoMercyQueue.Core.Resources;

namespace NoMercy.Queue.MediaServer;

public static class ServiceRegistration
{
    public static IServiceCollection AddMediaServerQueue(this IServiceCollection services)
    {
        services.AddSingleton<IQueueContext>(_ => new EfQueueContextAdapter());
        services.AddSingleton<IConfigurationStore, MediaConfigurationStore>();
        services.AddScoped<QueuePayloadCompaction>();

        // TryAdd so this shares the same instance as the encoder's own
        // default registration (see AddNoMercyEncoder) regardless of call order.
        services.TryAddSingleton<MediaActivityMonitor>();
        services.AddSingleton<IWorkerActivityGate, MediaPlaybackActivityGate>();
        services.AddSingleton(sp =>
        {
            EncoderResourceConfig resources = sp.GetRequiredService<
                IOptions<EncoderResourceConfig>
            >().Value;

            return new ResourceBudgetOptions(
                CpuHeadroomPercent: resources.EncoderCpuHeadroomPercent,
                GpuHeadroomPercent: resources.EncoderGpuHeadroomPercent,
                MinFreeMemoryMb: resources.EncoderMinFreeMemoryMb,
                GpuEncodePercentPerLease: resources.EncoderGpuPercentPerEncode
            );
        });
        services.AddSingleton<QueueRunner>(sp =>
        {
            IQueueContext queueContext = sp.GetRequiredService<IQueueContext>();
            IConfigurationStore configStore = sp.GetRequiredService<IConfigurationStore>();
            ILoggerFactory loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            IServiceScopeFactory scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
            NmSystem.Lifecycle.IServerPhaseTracker? phaseTracker =
                sp.GetService<NmSystem.Lifecycle.IServerPhaseTracker>();
            IResourceBudget? resourceBudget = sp.GetService<IResourceBudget>();
            IWorkerActivityGate? activityGate = sp.GetService<IWorkerActivityGate>();
            RuntimeServerSettings rs = sp.GetRequiredService<RuntimeServerSettings>();
            QueueConfiguration configuration = new()
            {
                WorkerCounts = new()
                {
                    [rs.LibraryWorkers.Key] = rs.LibraryWorkers.Value,
                    [rs.ImportWorkers.Key] = rs.ImportWorkers.Value,
                    [rs.ExtrasWorkers.Key] = rs.ExtrasWorkers.Value,
                    [rs.EncoderWorkers.Key] = rs.EncoderWorkers.Value,
                    [rs.GpuEncoderWorkers.Key] = rs.GpuEncoderWorkers.Value,
                    [rs.CpuEncoderWorkers.Key] = rs.CpuEncoderWorkers.Value,
                    [rs.CronWorkers.Key] = rs.CronWorkers.Value,
                    [rs.ImageWorkers.Key] = rs.ImageWorkers.Value,
                    [rs.FileWorkers.Key] = rs.FileWorkers.Value,
                    [rs.MusicWorkers.Key] = rs.MusicWorkers.Value,
                    [rs.PaletteWorkers.Key] = rs.PaletteWorkers.Value,
                },
            };
            // "encoder" carries its own resource requirement now — the SAME row
            // that decomposes also runs each bundle's ffmpeg inline, so the one
            // queue that used to be pure (cheap, ungated) dispatch work is also
            // where the actual encode executes. See VideoEncodeJob.ResourceRequirement.
            IReadOnlySet<string> resourceAwareQueues = new HashSet<string>
            {
                rs.EncoderWorkers.Key,
                rs.GpuEncoderWorkers.Key,
                rs.CpuEncoderWorkers.Key,
            };

            IReadOnlyDictionary<string, NmSystem.Lifecycle.BootStage> queueReadyStages =
                BuildQueueReadyStages(rs);

            return new(
                queueContext,
                configuration,
                loggerFactory,
                configStore,
                scopeFactory,
                phaseTracker,
                resourceBudget,
                resourceAwareQueues,
                activityGate,
                queueReadyStages
            );
        });
        services.AddSingleton<JobDispatcher>(sp => sp.GetRequiredService<QueueRunner>().Dispatcher);

        // Phase 4.14 — orphan job recovery on boot. This hosted service's
        // StartAsync only runs once ASP.NET Core starts the host (RunHost /
        // RunWithHttpsRestart), which happens AFTER ServerBootstrapper calls
        // QueueRunner.Initialize() (and therefore after Initialize's
        // ResetAllReservedJobs() has already cleared every ReservedAt). Do not
        // assume this service sees reservations Initialize() hasn't touched
        // yet — ordering here is host-startup order, not registration order.
        services.AddHostedService<OrphanJobRecoveryHostedService>();

        // Stuck-reservation reaper — runs periodically for the lifetime of the
        // process so a job that hangs mid-flight (not just one interrupted by
        // a crash) doesn't hold its queue slot forever. Encoder queues are
        // excluded by design; see the class doc for why a wall-clock cutoff is
        // unsafe for them.
        services.AddHostedService<StuckReservationReaperHostedService>();

        // One-time rewrite of payloads that carried their input inline. Reclaims
        // the space; the rows it has not reached yet still run correctly.
        services.AddHostedService<QueuePayloadCompactionHostedService>();

        return services;
    }

    /// <summary>
    /// Every queue used to default to <c>BootStage.All</c> — Essential | Auth |
    /// Binaries | Network | Registered — regardless of what its jobs actually
    /// touch. Auth, Network and Registered describe this server's relationship
    /// with nomercy-tv (login, IP discovery, cloud registration); most queues
    /// never call any of that. Binaries now marks the moment ffmpeg/ffprobe are
    /// on disk (see <c>FfmpegBinaryReadinessService</c> in NoMercy.Encoder), not
    /// when the whole binary bundle — including a multi-gigabyte whisper model
    /// and tesseract language data — finishes, so listing it below no longer
    /// means "wait for everything".
    /// <para>
    /// Essential (schema/settings) is the one dependency every queue genuinely
    /// shares — a worker cannot reserve a job before the queue.db schema exists.
    /// </para>
    /// </summary>
    /// <remarks>Internal (not private) so <c>NoMercy.Tests.Queue</c> can assert the
    /// exact per-queue combination without building the full DI graph this method
    /// is normally called from.</remarks>
    internal static IReadOnlyDictionary<string, NmSystem.Lifecycle.BootStage> BuildQueueReadyStages(
        RuntimeServerSettings rs
    )
    {
        NmSystem.Lifecycle.BootStage essential = NmSystem.Lifecycle.BootStage.Essential;
        NmSystem.Lifecycle.BootStage ffprobe = essential | NmSystem.Lifecycle.BootStage.Binaries;
        NmSystem.Lifecycle.BootStage remoteMetadata =
            essential | NmSystem.Lifecycle.BootStage.Auth | NmSystem.Lifecycle.BootStage.Network;

        // library: MediaScan calls FfProbe.CreateAsync on every candidate file and
        // ScanVideoFolder/ScanAudioFolder search TMDB — needs both ffprobe and the
        // server's own auth+network to reach it.
        NmSystem.Lifecycle.BootStage libraryReady = ffprobe | remoteMetadata;

        // import: TMDB/TVDB metadata only (append_to_response) — no local file I/O,
        // downstream ffprobe work (chapters, colors, file matching) runs on its own
        // queues under their own gate.
        NmSystem.Lifecycle.BootStage importReady = remoteMetadata;

        // file: FileRepository/FileListService match files to DB entries via
        // FfProbe.CreateAsync — local-only, no remote calls.
        NmSystem.Lifecycle.BootStage fileReady = ffprobe;

        // extras: chapter/color extraction shells out to ffmpeg; subtitle
        // acquisition (OpenSubtitles) needs network. No server auth token is used
        // by either.
        NmSystem.Lifecycle.BootStage extrasReady = ffprobe | NmSystem.Lifecycle.BootStage.Network;

        // music: MusicLogic probes files via FfProbe.CreateAsync and looks up
        // MusicBrainz/AcoustID — same shape as library.
        NmSystem.Lifecycle.BootStage musicReady = ffprobe | remoteMetadata;

        // image: downloads artwork from TMDB/FanArt/CoverArt — remote calls, no
        // local media file ever touches ffmpeg.
        NmSystem.Lifecycle.BootStage imageReady = remoteMetadata;

        // palette: reads an already-downloaded image off disk and extracts a color
        // palette in managed code — needs only the schema.
        NmSystem.Lifecycle.BootStage paletteReady = essential;

        // cron: dispatches scheduled jobs onto their own queues; it does not itself
        // call TMDB, ffprobe or the registration API — the job it fires does, under
        // that job's own queue's gate.
        NmSystem.Lifecycle.BootStage cronReady = essential;

        // Encoder queues need ffmpeg on disk and GPU/encoder detection
        // (BootStage.Hardware) — not this server's auth/network/registration
        // state, which has nothing to do with spawning a local ffmpeg process.
        NmSystem.Lifecycle.BootStage encoderReady = ffprobe | NmSystem.Lifecycle.BootStage.Hardware;

        return new Dictionary<string, NmSystem.Lifecycle.BootStage>
        {
            [rs.LibraryWorkers.Key] = libraryReady,
            [rs.ImportWorkers.Key] = importReady,
            [rs.ExtrasWorkers.Key] = extrasReady,
            [rs.FileWorkers.Key] = fileReady,
            [rs.MusicWorkers.Key] = musicReady,
            [rs.ImageWorkers.Key] = imageReady,
            [rs.PaletteWorkers.Key] = paletteReady,
            [rs.CronWorkers.Key] = cronReady,
            [rs.EncoderWorkers.Key] = encoderReady,
            [rs.GpuEncoderWorkers.Key] = encoderReady,
            [rs.CpuEncoderWorkers.Key] = encoderReady,
        };
    }
}
