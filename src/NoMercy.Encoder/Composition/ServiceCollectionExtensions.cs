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

using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using NoMercy.Encoder.Analysis;
using NoMercy.Encoder.BuildingBlocks;
using NoMercy.Encoder.BuildingBlocks.Drm;
using NoMercy.Encoder.Bundle;
using NoMercy.Encoder.Codecs;
using NoMercy.Encoder.Commands;
using NoMercy.Encoder.ContentAnalysis;
using NoMercy.Encoder.ContentAnalysis.Fingerprinting;
using NoMercy.Encoder.Devices;
using NoMercy.Encoder.Distribution;
using NoMercy.Encoder.Execution;
using NoMercy.Encoder.Hardware;
using NoMercy.Encoder.Hdr;
using NoMercy.Encoder.Infrastructure;
using NoMercy.Encoder.Jobs;
using NoMercy.Encoder.LiveTranscode;
using NoMercy.Encoder.Metadata;
using NoMercy.Encoder.Naming;
using NoMercy.Encoder.Notifications;
using NoMercy.Encoder.Orchestration;
using NoMercy.Encoder.Output;
using NoMercy.Encoder.Pipeline;
using NoMercy.Encoder.Pipeline.Optimizer;
using NoMercy.Encoder.Pipeline.Stages;
using NoMercy.Encoder.PostProcess;
using NoMercy.Encoder.Profiles;
using NoMercy.Encoder.Reconciliation;
using NoMercy.Encoder.Startup;
using NoMercy.Encoder.Strategies;
using NoMercy.Encoder.Strategies.Audio;
using NoMercy.Encoder.Strategies.Dash;
using NoMercy.Encoder.Strategies.Hls;
using NoMercy.Encoder.Strategies.Mkv;
using NoMercy.Encoder.Strategies.Mp4;
using NoMercy.Encoder.Subscribers;
using NoMercy.Encoder.Subtitles;
using NoMercy.NmSystem.Extensions;
using NoMercy.NmSystem.Lifecycle;
using NoMercy.Resources;
using NoMercy.Storage;

namespace NoMercy.Encoder.Composition;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddNoMercyEncoder(
        this IServiceCollection services,
        Action<EncoderOptions>? configure = null
    )
    {
        // Configuration
        EncoderOptions opts = new();
        configure?.Invoke(opts);
        services.AddSingleton(opts);

        // IHttpClientFactory is needed by the self-registration service and
        // by HttpRemoteWorker callers. Safe to call multiple times — the
        // factory registration is idempotent.
        services.AddHttpClient();

        // Infrastructure
        // IStorage is the cross-project filesystem abstraction. Registered
        // via TryAdd inside AddNoMercyStorage so a host that already
        // configured allowed roots (or swapped in a non-local backend)
        // wins. Empty allowed-roots list keeps the path guard permissive
        // until consumers populate it during the encoder Phase 0.2 sweep.
        services.AddNoMercyStorage();

        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton<IMediaAnalyzer, MediaAnalyzer>();

        // Codecs
        services.AddSingleton<CodecRegistry>();
        services.AddSingleton<ICodecResolver, CodecResolver>();
        services.AddSingleton<IHardwarePreferenceResolver, HardwarePreferenceResolver>();
        services.AddSingleton<IBitDepthPolicyResolver, BitDepthPolicyResolver>();

        // Quality scalers — specific encoders first, LinearQualityScaler last
        // (it is the unconditional fallback: Supports always returns true).
        services.AddSingleton<IQualityScaler, NvencQualityScaler>();
        services.AddSingleton<IQualityScaler, QsvQualityScaler>();
        services.AddSingleton<IQualityScaler, VideoToolboxQualityScaler>();
        services.AddSingleton<IQualityScaler, AmfAv1QualityScaler>();
        services.AddSingleton<IQualityScaler, SvtAv1QualityScaler>();
        services.AddSingleton<IQualityScaler, LinearQualityScaler>();
        services.AddSingleton<IQualityScalerResolver, QualityScalerResolver>();

        // Hardware
        services.AddSingleton<IHardwareDetector, PlatformHardwareDetector>();
        // Driver fingerprint lives in AppDbContext.Configuration so a server
        // admin can move the install without dragging a sidecar JSON file.
        // The DB store auto-imports any legacy driver_fingerprint.json on
        // first load and deletes the file on success.
        services.AddSingleton<IDriverFingerprintStore, DbDriverFingerprintStore>();
        services.AddSingleton<IDriverChangeDetector, DriverChangeDetector>();
        services.AddSingleton<FfmpegCapabilities>();
        services.AddSingleton<IFfmpegCapabilities>(sp =>
            sp.GetRequiredService<FfmpegCapabilities>()
        );

        // Authoritative real-init probe for hardware encoders — see
        // HardwareEncoderProbe for why ffmpeg's compiled-in encoder list is
        // not sufficient signal on its own. Runs once at boot from
        // HardwareInitializationService; PlanStage gates encoder selection
        // on the resulting IHardwareCapabilities.UsableHardwareEncoders set.
        services.AddSingleton<IHardwareEncoderProbe, HardwareEncoderProbe>();

        // IHardwareCapabilities — forwards every read through the holder so
        // singletons constructed before HIS finishes detection (most notably
        // HardwareBenchmark) pick up the GPU list once detection completes.
        //
        // Earlier this was a factory that captured Holder.Current at first
        // resolution. That always returned null at startup and cached an
        // empty CPU-only HardwareCapabilities forever — so the benchmark
        // scheduler saw zero GPUs even after HIS detected an Nvidia / AMD /
        // Intel card, and ran software calibrations only. The holder-backed
        // forwarder fixes the staleness without disturbing the no-cycle
        // dependency graph below.
        //
        // The dependency graph does NOT pass through HardwareInitializationService.
        // HIS itself transitively depends on IHardwareCapabilities (via
        // BenchmarkJobTracker → HardwareBenchmark), so resolving capabilities
        // through HIS forms a cycle that crashes the test host with a stack
        // overflow. The holder is a tiny mutable singleton that HIS writes to
        // once detection completes; before that, callers receive a CPU-only
        // default that lets pipeline construction succeed without GPU info.
        services.AddSingleton<HardwareCapabilitiesHolder>();
        services.AddSingleton<IHardwareCapabilities, HolderBackedHardwareCapabilities>();

        // IResourceMonitor — NvmlGpuSampler extends ProcessResourceMonitor and
        // adds real per-process GPU encoder utilization via nvidia-smi shell-out.
        // It degrades gracefully to empty samples when nvidia-smi is absent or
        // fails (non-NVIDIA hosts, GPU-less servers). All CPU/memory methods are
        // inherited from ProcessResourceMonitor unchanged.
        services.AddSingleton<IResourceMonitor, NvmlGpuSampler>();

        // NVENC session cap — enforced at dispatch time in EncodingOrchestrator
        // before ffmpeg is spawned. Consumer NVIDIA GPUs cap at 3 concurrent sessions;
        // pro cards report int.MaxValue (no cap). NvencSessionCap reads the limit from
        // IHardwareCapabilities.Gpus[*].MaxEncoderSessions.
        services.AddSingleton<INvencSessionCap, NvencSessionCap>();

        // IResourceBudget — holds IHardwareCapabilities live so the GPU list
        // is re-read after async detection completes. The previous factory
        // captured hw.Gpus eagerly (an empty list at startup since
        // HardwareInitializationService runs as a hosted service AFTER the DI
        // graph is built), which silently produced a semaphore map with zero
        // entries. Every encoder-gpu task then crashed on first job pick
        // because GetGpuSemaphore threw "device 'h264_nvenc' is not
        // registered" — see UnobservedTaskException entries in encoder logs
        // before this fix.
        // Activate the live-headroom gate with the record's protective defaults
        // (75% CPU / 80% GPU / 1 GB free). Without this registration the budget fell
        // back to ResourceBudgetOptions.Disabled — the gate was built but dormant, so
        // the encoder never backed off and could saturate a machine the user is
        // actively using. TODO: surface CpuHeadroomPercent as a dashboard setting so a
        // dedicated-encode-box operator can raise it toward 100 (max throughput).
        services.TryAddSingleton(new ResourceBudgetOptions());

        services.AddSingleton<IResourceBudget>(sp =>
        {
            IHardwareCapabilities hw = sp.GetRequiredService<IHardwareCapabilities>();
            IResourceMonitor monitor = sp.GetRequiredService<IResourceMonitor>();
            ResourceBudgetOptions options =
                sp.GetService<ResourceBudgetOptions>() ?? ResourceBudgetOptions.Disabled;
            ILogger<ResourceBudget> logger = sp.GetRequiredService<ILogger<ResourceBudget>>();
            return new ResourceBudget(hw, monitor, options, logger);
        });

        // Startup — register concrete first so IHostedService resolves same instance
        services.AddSingleton<HardwareInitializationService>();
        services.AddHostedService(sp => sp.GetRequiredService<HardwareInitializationService>());

        // Marks BootStage.Binaries as soon as ffmpeg/ffprobe are on disk, independent
        // of whisper-model/tesseract downloads that run after them and can take
        // minutes — see FfmpegBinaryReadinessService for why every ffprobe-dependent
        // queue needs this signal decoupled from the full binary bundle.
        services.AddHostedService<FfmpegBinaryReadinessService>();

        // Hardware benchmark runs lazily — defers past ApplicationStarted, then
        // waits for the encoder to be idle (no active streams/jobs) before
        // spawning calibration ffmpeg processes. Startup is never blocked and
        // user-visible work never shares CPU/GPU with the benchmark.
        services.TryAddSingleton<IEncoderActivityProbe, NullEncoderActivityProbe>();
        services.AddHostedService<HardwareBenchmarkBackgroundService>();

        // Periodic recalibration — checks every hour whether the speed index
        // is stale (> 30 days) or a driver change occurred; defers to idle
        // before running. Separate from HardwareBenchmarkBackgroundService
        // which only handles the first-boot case.
        services.AddHostedService<HardwareBenchmarkRecalibrationService>();

        // HDR
        services.AddTransient<ITonemapSelector, TonemapSelector>();

        // Naming resolvers
        services.AddSingleton<IMediaKeyResolver, MediaKeyResolver>();
        services.AddSingleton<IOutputNamingResolver, OutputNamingResolver>();

        // Metadata injector and merger
        services.AddSingleton<IMetadataInjector, MetadataInjector>();
        services.AddSingleton<IMetadataMerger, MetadataMerger>();

        // Bundle writers
        services.AddSingleton<IBundleGarbageCollector, BundleGarbageCollector>();

        // Reconstruction blueprint — one .nomercy.json per media item,
        // replacing the old per-preset manifest.json + reconstruction.json
        // pair. See .claude/specs/reconstruction-blueprint/SPEC.md.
        services.AddSingleton<IMediaBlueprintBuilder, MediaBlueprintBuilder>();
        services.AddSingleton<IMediaBlueprintWriter, MediaBlueprintWriter>();

        // Encode-output reconciliation — decides, before any ffmpeg command is
        // built, whether a re-dispatched file needs a full re-encode, only its
        // missing pieces, or nothing at all. Stateless; safe as a singleton.
        services.AddSingleton<IEncodeReconciler, EncodeReconciler>();

        // Profiles
        services.AddTransient<IProfileValidator, BackedProfileValidator>();
        services.AddTransient<INamePresetResolver, ByNamePresetResolver>();
        services.AddSingleton<IProfileSignatureVerifier, ProfileSignatureVerifier>();

        // Execution
        services.AddTransient<IFfmpegExecutor>(sp => new FfmpegExecutor(
            sp.GetRequiredService<IProcessRunner>(),
            sp.GetRequiredService<ILogger<FfmpegExecutor>>(),
            () =>
            {
                ILiveStreamingService? live = sp.GetService<ILiveStreamingService>();
                return live?.ActiveSessionIds.Any(sessionId =>
                        live.TryGetRuntime(sessionId, out LiveRuntimeSession runtime)
                        && !runtime.IsAudioRenditionChild
                        && !runtime.IsComplete
                        && runtime.Session.State
                            is not (LiveSessionState.Error or LiveSessionState.Ended)
                        && runtime.Session.BufferAhead < TimeSpan.FromSeconds(5)
                    ) == true;
            },
            sp.GetRequiredService<NmSystem.Monitoring.MediaActivityMonitor>(),
            sp.GetRequiredService<ProcessThrottle>()
        ));
        services.AddTransient<ProgressParser>();
        // ProcessThrottle holds the set of suspended pids; must be Singleton so
        // suspend/resume operations across different callers see the same state.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            services.AddSingleton<IProcessSuspender, WindowsProcessSuspender>();
        else
            services.AddSingleton<IProcessSuspender, UnixProcessSuspender>();
        services.AddSingleton<ProcessThrottle>();
        services.AddSingleton<IEncoderProcessRegistry, EncoderProcessRegistry>();

        // Checkpoint persistence
        services.AddTransient<ICheckpointStore, JsonCheckpointStore>();

        // Content intelligence (OCR / Whisper / crop detection)
        services.TryAddSingleton(sp =>
        {
            HttpClient client = new();
            client.WithNoMercyUserAgent();
            return client;
        });
        // ITesseractModelDownloader is implemented in NoMercy.Setup (which owns
        // release/manifest signature verification) and cannot be referenced here — the
        // Service host registers the real one after this call, replacing this default
        // via standard DI last-wins, same as IOpenSubtitlesProvider below.
        services.TryAddSingleton<ITesseractModelDownloader, NoOpTesseractModelDownloader>();
        services.AddTransient<ITesseractModelManager, TesseractModelManager>();
        services.AddTransient<ISubtitleOcrEngine, SubtitleOcrEngine>();
        services.AddSingleton<ISubtitleRouter, SubtitleRouter>();
        services.AddTransient<IWhisperTranscriber, WhisperTranscriber>();
        services.AddSingleton<CropResultCache>();
        services.AddTransient<ICropDetector, CropDetector>();
        services.AddTransient<IEncodeYieldProbe, EncodeYieldProbe>();

        // Self-sufficient default: ChromaprintFingerprinter defers to active
        // playback via MediaActivityMonitor. TryAdd so a host that also calls
        // AddMediaServerQueue() (the primary owner) shares that same instance
        // regardless of which registration runs first.
        services.TryAddSingleton<NmSystem.Monitoring.MediaActivityMonitor>();
        services.AddTransient<IAudioFingerprinter, ChromaprintFingerprinter>();
        services.AddTransient<IIntroDetector, ChromaprintIntroDetector>();

        // Notifications — plugins can replace INotificationDispatcher to swap
        // webhooks for Discord/Slack/email/etc.
        services.AddSingleton<INotificationDispatcher, WebhookNotificationDispatcher>();

        // Subtitle acquisition — adapter + service. IOpenSubtitlesProvider has a
        // no-op default registered here so encoder-only test contexts always
        // resolve. The Service host registers the real XML-RPC provider after
        // this call, replacing the no-op via standard DI last-wins.
        services.AddSingleton<IOpenSubtitlesProvider, NoOpOpenSubtitlesProvider>();
        services.AddTransient<IOpenSubtitlesAdapter, OpenSubtitlesAdapter>();
        services.AddTransient<ISubtitleAcquisitionService, SubtitleAcquisitionService>();

        // Subtitle burn-in filter builders
        services.AddTransient<AssBurnInFilterBuilder>();
        services.AddTransient<PgsBurnInFilterBuilder>();

        // Building blocks
        services.AddTransient<IFilterGraphBuilder, FilterGraphBuilder>();
        services.AddTransient<IPlaylistGenerator, PlaylistGenerator>();
        services.AddTransient<ISubtitleExtractor, SubtitleExtractor>();
        services.AddTransient<IFontExtractor, FontExtractor>();
        services.AddTransient<IChapterWriter, ChapterWriter>();
        services.AddTransient<IThumbnailGenerator, ThumbnailGenerator>();
        services.AddTransient<ISpriteSheetRefresher, SpriteSheetRefresher>();
        services.AddTransient<IHlsVariantAnalyzer, HlsVariantAnalyzer>();
        services.AddTransient<IAbrLadderGenerator, AbrLadderGenerator>();
        services.AddSingleton<ILadderGenerator, LadderGenerator>();

        // DRM processors — plugins can register additional processors for
        // custom schemes. IEnumerable<IDrmProcessor> resolution picks the
        // one whose Method matches the profile's DrmConfig.
        services.AddTransient<IDrmProcessor, Aes128HlsDrmProcessor>();
        // CENC processor handles DASH raw-key packaging via shaka-packager.
        // PrepareAsync is a no-op for CENC (post-encode); callers that need
        // CENC packaging resolve CencDrmProcessor directly and call PackageAsync.
        services.AddTransient<CencDrmProcessor>();

        // Output strategies — resolved via IOutputStrategyFactory from DI.
        // Plugins can register additional IOutputStrategy impls and the factory
        // will prefer them (last registration wins).
        services.AddTransient<IOutputStrategy, HlsOutputStrategy>();
        services.AddTransient<IOutputStrategy, MkvOutputStrategy>();
        services.AddTransient<IOutputStrategy, Mp4OutputStrategy>();
        services.AddTransient<IOutputStrategy, DashOutputStrategy>();
        services.AddTransient<IOutputStrategy, Mp3OutputStrategy>();
        services.AddTransient<IOutputStrategy, FlacOutputStrategy>();
        services.AddTransient<IOutputStrategy, OggOutputStrategy>();
        services.AddTransient<IOutputStrategy, AudioHlsOutputStrategy>();
        services.AddTransient<IOutputStrategyFactory, OutputStrategyFactory>();

        // Pipeline stages — each concrete stage is also reachable via its
        // named role interface so strategies can resolve by role without
        // coupling to the concrete class.
        services.AddTransient<AnalyzeStage>();
        services.AddTransient<IAnalysisStage>(sp => sp.GetRequiredService<AnalyzeStage>());
        services.AddTransient<ValidateStage>();
        services.AddTransient<IValidationStage>(sp => sp.GetRequiredService<ValidateStage>());
        services.AddTransient<PlanStage>();
        services.AddTransient<IPlanStage>(sp => sp.GetRequiredService<PlanStage>());
        services.AddTransient<BuildStage>();
        services.AddTransient<IBuildStage>(sp => sp.GetRequiredService<BuildStage>());
        services.AddSingleton<IPlanResultProjector, PlanResultProjector>();
        services.AddTransient<ExecuteStage>();
        services.AddTransient<IExecutionStage>(sp => sp.GetRequiredService<ExecuteStage>());
        services.AddTransient<FinalizeStage>();
        services.AddTransient<IFinalizeStage>(sp => sp.GetRequiredService<FinalizeStage>());

        // Optimizer
        services.AddTransient<ExecutionGraphBuilder>();
        services.AddTransient<GroupingStrategy>();
        services.AddTransient<ResourceAllocator>();
        services.AddTransient<CostEstimator>();

        // Encoder
        services.AddTransient<IEncoder, Pipeline.Encoder>();

        // Strategies — one per {OutputFormat, EncodeMode} tuple.
        // Plugins can register additional IEncodingStrategy impls and the resolver
        // will pick them up automatically (last registration wins).
        services.AddTransient<IEncodingStrategy, HlsSinglePassStrategy>();
        services.AddTransient<IEncodingStrategy, HlsTwoPassStrategy>();
        services.AddTransient<IEncodingStrategy, MkvStrategy>();
        services.AddTransient<IEncodingStrategy, Mp4SinglePassStrategy>();
        services.AddTransient<IEncodingStrategy, Mp4TwoPassStrategy>();
        services.AddTransient<IEncodingStrategy, DashSinglePassStrategy>();
        services.AddTransient<IEncodingStrategy, DashTwoPassStrategy>();
        services.AddTransient<IEncodingStrategy, Mp3Strategy>();
        services.AddTransient<IEncodingStrategy, FlacStrategy>();
        services.AddTransient<IEncodingStrategy, OggStrategy>();
        services.AddTransient<IEncodingStrategy, AudioHlsStrategy>();
        services.AddTransient<IStrategyResolver, StrategyResolver>();
        services.AddTransient<IEncodingOrchestrator, EncodingOrchestrator>();

        // Device capability negotiation
        services.AddSingleton<IDeviceCapabilityRegistry, DeviceCapabilityRegistry>();
        services.AddSingleton<IDeviceAwareVariantSelector, DeviceAwareVariantSelector>();

        // Live Transcoding
        // NoOp transport is the default — the Api layer registers SignalRLiveSessionTransport
        // as the real implementation. TryAdd ensures it never overwrites the Api registration.
        services.TryAddSingleton<ILiveSessionTransport, NoOpLiveSessionTransport>();

        services.AddSingleton<IPlaybackDecisionEngine, PlaybackDecisionEngine>();
        services.AddSingleton<LiveSessionLimits>();
        services.AddSingleton<ISessionManager>(sp => new SessionManager(
            sp.GetRequiredService<LiveSessionLimits>()
        ));

        // Ties a session's lifetime to the SignalR connection watching it so a
        // hard tab-close / navigation disposes it after a short grace window,
        // instead of leaking against the concurrency cap until the idle reaper.
        services.AddSingleton<ILiveSessionPresenceTracker, LiveSessionPresenceTracker>();
        services.AddTransient<ILiveQualitySelector, LiveQualitySelector>();
        services.AddTransient<BufferManager>();
        services.AddSingleton<ISpeedIndexStore, JsonSpeedIndexStore>();
        services.AddSingleton<IHardwareBenchmark, HardwareBenchmark>();
        services.AddSingleton<IBenchmarkJobTracker, BenchmarkJobTracker>();

        // Consumers read the benchmark's current index at use time. A
        // SpeedIndex singleton would freeze the boot-time snapshot (empty on a
        // fresh server) until restart, missing every later benchmark run.
        services.AddSingleton<ISpeedIndexProvider, BenchmarkSpeedIndexProvider>();
        services.AddTransient<ILiveFfmpegRunner, LiveFfmpegRunner>();
        // Stateless — reads the scratch directory fresh on every call, so one
        // shared instance across every live session is safe.
        services.AddSingleton<ILiveSegmentInventory, LiveSegmentInventory>();
        services.AddSingleton<ILiveEncoder, LiveEncoder>();
        services.AddSingleton<ILiveStreamingService, LiveStreamingService>();
        services.AddTransient<ILivePlaylistBuilder, LivePlaylistBuilder>();

        // At startup: delete any lts-* orphan dirs left by a previous crash.
        services.AddHostedService<LiveTranscodeOrphanSweeper>();

        // At startup: delete batch-encode working dirs (nomercy-enc-* and
        // output-mirrored show dirs) left under the transcode root by a crash.
        services.AddHostedService<TranscodeRootOrphanSweeper>();

        // Every 30 s: evict sessions with no playlist/segment hit in the last N minutes.
        services.AddHostedService<LiveSessionIdleReaper>();

        // Every 5 s: suspend over-buffered encoders, resume drained ones, drop quality when buffer is low.
        services.AddHostedService<BufferAdaptiveService>();

        // Distribution — LocalWorkerDispatcher is the default; remote workers
        // land as a follow-up behind a feature flag. Plugins can register a
        // replacement IWorkerDispatcher to change behavior project-wide.
        services.AddTransient<LocalWorkerDispatcher>();
        services.AddTransient<ISourceDurationProbe, MediaAnalyzerSourceDurationProbe>();
        services.AddTransient<IWorkerAssigner, WorkerAssigner>();

        // Registry is a singleton so registrations accumulate across the
        // process lifetime. InMemoryRemoteWorkerRegistry is always registered
        // as the runtime backing store. In coordinator mode (signing key set
        // + no CoordinatorUrl) we wrap it with JsonRemoteWorkerRegistry so
        // worker identities survive a coordinator restart. Worker mode and
        // standalone installs use the in-memory registry directly.
        services.AddSingleton<InMemoryRemoteWorkerRegistry>();
        services.AddSingleton<IRemoteWorkerRegistry>(sp =>
        {
            EncoderOptions encoderOpts = sp.GetRequiredService<EncoderOptions>();

            if (encoderOpts.IsCoordinatorMode)
            {
                string persistPath = encoderOpts.WorkerRegistryPath;
                return new JsonRemoteWorkerRegistry(
                    inner: sp.GetRequiredService<InMemoryRemoteWorkerRegistry>(),
                    filePath: persistPath,
                    httpClientFactory: sp.GetRequiredService<IHttpClientFactory>(),
                    serializer: sp.GetRequiredService<ITaskSerializer>(),
                    signingKey: encoderOpts.GetDistributedEncodingSigningKey(),
                    logger: sp.GetRequiredService<ILogger<JsonRemoteWorkerRegistry>>(),
                    storage: sp.GetRequiredService<IStorage>()
                );
            }

            return sp.GetRequiredService<InMemoryRemoteWorkerRegistry>();
        });

        // Signed transport between coordinator and workers.
        services.AddTransient<ITaskSerializer, TaskSerializer>();

        // Source fetching: when the worker can't see the task's input
        // path locally, it downloads from the coordinator via
        // HttpSourceFetcher. Swapped to NullSourceFetcher when the
        // install uses shared storage and no fetching is needed.
        services.AddTransient<ISourceFetcher, HttpSourceFetcher>();

        // Resolves signed worker payloads into dispatch-ready tasks
        // (HMAC verification + local source fetch), lifted out of the
        // transport controller for testability.
        services.AddTransient<IWorkerInputResolver, WorkerInputResolver>();

        // Per-task progress: workers push to the coordinator via
        // HttpTaskProgressSink (only actually pushes when CoordinatorUrl
        // is set). Coordinator-side InMemoryTaskProgressStore holds the
        // latest snapshot per task so the dashboard can read live progress.
        services.AddSingleton<InMemoryTaskProgressStore>();
        services.AddSingleton<ITaskProgressStore>(sp =>
            sp.GetRequiredService<InMemoryTaskProgressStore>()
        );
        services.AddTransient<ITaskProgressSink, HttpTaskProgressSink>();

        // Capability probe — deferred 5 s after ApplicationStarted.
        // Checks fpcalc, Whisper model, Tesseract eng.traineddata, and
        // validates required FFmpeg filters/muxers. Reuses FfmpegCapabilities
        // for filters/protocols to avoid duplicate shell calls.
        services.AddSingleton<IFfmpegCapabilityProbe, FfmpegCapabilityProbe>();
        // The probe's hosted service gates on boot completion via
        // IServerPhaseTracker. TryAdd a default so the encoder module is
        // self-contained; the Service host registers the process-wide shared
        // tracker (same instance) and wins last in production.
        services.TryAddSingleton<IServerPhaseTracker>(sp =>
            ServerPhaseTracker.Shared(sp.GetService<ILogger<ServerPhaseTracker>>())
        );
        services.AddHostedService<FfmpegCapabilityProbeBackgroundService>();

        // BuiltinPresetSeeder retired — Service-side EncodingPresetsSeed
        // is the single source of truth for the curated preset library.

        // Self-registration background service — no-ops on standalone
        // installs (when CoordinatorUrl is not set). Safe to always
        // register; the service exits cleanly in that case.
        // Its client signs every request: the coordinator's HMAC middleware
        // rejects unsigned calls to /api/v1/distribution/ with 401.
        services.AddTransient<HmacSigningHandler>();
        services
            .AddHttpClient(WorkerSelfRegistrationService.HttpClientName)
            .AddHttpMessageHandler<HmacSigningHandler>();
        services.AddHostedService<WorkerSelfRegistrationService>();
        // The remote dispatcher is the public face — it transparently falls
        // back to the local dispatcher when no remote workers are registered,
        // which is the default behavior today.
        services.AddTransient<IWorkerDispatcher>(sp => new RemoteWorkerDispatcher(
            sp.GetRequiredService<IRemoteWorkerRegistry>(),
            sp.GetRequiredService<IWorkerAssigner>(),
            sp.GetRequiredService<LocalWorkerDispatcher>(),
            sp.GetRequiredService<ILogger<RemoteWorkerDispatcher>>()
        ));

        // Disc ripping registrations moved to NoMercy.OpticalMedia/Composition/
        // ServiceCollectionExtensions.AddNoMercyOpticalMedia(); call that from
        // the host alongside AddNoMercyEncoder().

        // V3 event-bus subscribers — registered as singletons so the activator
        // can resolve them once at start-up (their constructors subscribe to
        // the event bus). Auto-encode dispatch lives solely in
        // NoMercy.MediaProcessing.EventHandlers.AutoEncodeSubscriber, gated
        // per-library. Each subscriber here has its own opt-out flag on
        // EncoderOptions for hard disable.
        services.AddSingleton<IntroDetectSubscriber>();
        services.AddSingleton<CropDetectSubscriber>();
        services.AddHostedService<V3SubscriberActivator>();

        return services;
    }
}
