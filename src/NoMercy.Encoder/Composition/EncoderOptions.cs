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

using System.Text;
using NoMercy.NmSystem.Information;
using NoMercy.Storage;

namespace NoMercy.Encoder.Composition;

public class EncoderOptions
{
    public string? FfmpegPathOverride { get; set; }
    public string? FfprobePathOverride { get; set; }

    /// <summary>
    /// Resolved FFmpeg path: uses the configured override (set via AppFiles.FfmpegPath at startup).
    /// Throws if not configured — callers must register via AddNoMercyEncoder with the path set.
    /// </summary>
    public string FfmpegPath =>
        FfmpegPathOverride
        ?? throw new InvalidOperationException(
            "FfmpegPathOverride not configured. Set it via AddNoMercyEncoder()."
        );

    /// <summary>
    /// Resolved FFprobe path: uses the configured override (set via AppFiles.FfProbePath at startup).
    /// Throws if not configured — callers must register via AddNoMercyEncoder with the path set.
    /// </summary>
    public string FfprobePath =>
        FfprobePathOverride
        ?? throw new InvalidOperationException(
            "FfprobePathOverride not configured. Set it via AddNoMercyEncoder()."
        );

    /// <summary>
    /// Override path to the shaka-packager binary. When null, resolution falls
    /// back to a binary named <c>packager</c> (or <c>packager.exe</c> on Windows)
    /// alongside the configured <see cref="FfmpegPathOverride"/>, and then to
    /// the system PATH. Set explicitly during startup via
    /// <c>AddNoMercyEncoder(opts => opts.ShakaPackagerPathOverride = ...)</c>.
    /// </summary>
    public string? ShakaPackagerPathOverride { get; set; }

    /// <summary>
    /// Resolved shaka-packager path. Resolution order:
    ///   1. <see cref="ShakaPackagerPathOverride"/> (explicit config)
    ///   2. Sibling of <see cref="FfmpegPathOverride"/> named <c>packager[.exe]</c>
    ///   3. <c>packager</c> on the system PATH (checked via <c>which</c>/<c>where</c>)
    /// Throws <see cref="InvalidOperationException"/> when nothing is found.
    /// </summary>
    public string GetShakaPackagerPath(IStorage storage)
    {
        if (!string.IsNullOrWhiteSpace(ShakaPackagerPathOverride))
            return ShakaPackagerPathOverride;

        // Try sibling of ffmpeg binary
        if (!string.IsNullOrWhiteSpace(FfmpegPathOverride))
        {
            string dir = Path.GetDirectoryName(FfmpegPathOverride) ?? string.Empty;
            string siblingName = OperatingSystem.IsWindows() ? "packager.exe" : "packager";
            string sibling = Path.Combine(dir, siblingName);
            if (storage.Exists(sibling))
                return sibling;
        }

        // Fall back to system PATH
        string pathEnv = OperatingSystem.IsWindows() ? "packager.exe" : "packager";
        string? onPath = FindOnPath(pathEnv, storage);
        if (onPath is not null)
            return onPath;

        throw new InvalidOperationException(
            "shaka-packager binary not found. Bundle it alongside ffmpeg or set "
                + "ShakaPackagerPathOverride in AddNoMercyEncoder()."
        );
    }

    private static string? FindOnPath(string binaryName, IStorage storage)
    {
        string pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        char sep = OperatingSystem.IsWindows() ? ';' : ':';
        foreach (string dir in pathVar.Split(sep, StringSplitOptions.RemoveEmptyEntries))
        {
            string full = Path.Combine(dir.Trim(), binaryName);
            if (storage.Exists(full))
                return full;
        }

        return null;
    }

    public int DefaultSegmentDurationSeconds { get; set; } = 4;
    public int MaxBufferAheadSeconds { get; set; } = 30;
    public int MinBufferAheadSeconds { get; set; } = 15;
    public int ProgressThrottleMs { get; set; } = 500;
    public string? SpeedIndexCachePath { get; set; }
    public bool AutoCalibrate { get; set; } = true;

    /// <summary>
    /// Directory that holds Tesseract *.traineddata files. The manager creates
    /// this folder on demand and downloads missing language models into it.
    /// Required for subtitle OCR (PGS / VobSub → WebVTT).
    /// </summary>
    public string? TesseractModelsDirectory { get; set; }

    /// <summary>
    /// Absolute path to the whisper.cpp GGML model (e.g. ggml-large-v3.bin).
    /// Required for speech-to-text subtitle generation.
    /// </summary>
    public string? WhisperModelPath { get; set; }

    /// <summary>
    /// Path to the stemsplit GGUF model; the analysis job passes its file name
    /// relative to the ffmpeg folder.
    /// </summary>
    public string? StemsplitModelPath { get; set; }

    /// <summary>
    /// Webhook URLs that receive a JSON POST for each encoder lifecycle event
    /// (started / completed / failed). Empty by default. Each URL is retried up
    /// to 3 times with exponential backoff; failures are logged and swallowed
    /// so a notification error never fails the encode.
    /// </summary>
    public IList<string> NotificationWebhookUrls { get; } = [];

    /// <summary>
    /// Parent directory for live transcode scratch folders. Each live session
    /// gets its own subfolder <c>{this}/{sessionId}</c> that holds the HLS
    /// playlist + .ts segments emitted by FFmpeg. Caller is responsible for
    /// cleanup when the session ends. Defaults to the app's controlled transcode
    /// cache (<see cref="AppFiles.TranscodePath"/>) so live scratch stays under
    /// the managed data folder instead of the system temp directory.
    /// </summary>
    public string? LiveTranscodeCachePath { get; set; }

    public string ResolvedLiveTranscodeCachePath =>
        LiveTranscodeCachePath ?? AppFiles.TranscodePath;

    /// <summary>
    /// Shared secret that signs distributed-encoding payloads between the
    /// coordinator and remote workers (see <see cref="NoMercy.Encoder.Distribution.TaskSerializer"/>).
    /// Must be identical on both sides — any divergence causes every task
    /// and every heartbeat to fail HMAC verification. Leave null on single-
    /// machine installs; distribution features stay disabled.
    /// </summary>
    public string? DistributedEncodingSigningKey { get; set; }

    /// <summary>
    /// True when a distributed-encoding signing key is configured — the API
    /// layer uses this to gate worker registration endpoints.
    /// </summary>
    public bool IsDistributedEncodingEnabled =>
        !string.IsNullOrWhiteSpace(DistributedEncodingSigningKey);

    /// <summary>
    /// Absolute path to the workers.json file used by
    /// <see cref="JsonRemoteWorkerRegistry"/> to persist worker registrations
    /// across coordinator restarts. Defaults to
    /// <c>%LOCALAPPDATA%/NoMercy/distribution/workers.json</c> (or the
    /// XDG-equivalent on Linux/macOS). Set this via
    /// <c>AddNoMercyEncoder(opts =&gt; opts.WorkerRegistryPath = ...)</c> to
    /// redirect persistence to a different location (e.g. alongside the
    /// media.db when the host configures a custom data root).
    /// </summary>
    public string WorkerRegistryPath { get; set; } =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NoMercy",
            "distribution",
            "workers.json"
        );

    /// <summary>
    /// True when this server is acting as a coordinator (not a worker).
    /// A coordinator has a signing key set and no <see cref="CoordinatorUrl"/>
    /// pointing to another host. In coordinator mode the
    /// <see cref="JsonRemoteWorkerRegistry"/> is registered so worker
    /// identities survive coordinator restarts.
    /// </summary>
    public bool IsCoordinatorMode =>
        IsDistributedEncodingEnabled && string.IsNullOrWhiteSpace(CoordinatorUrl);

    // ── Background-subscriber toggles ──────────────────────────────────────

    /// <summary>
    /// When true (default), <c>IntroDetectSubscriber</c> runs chromaprint
    /// fingerprinting + intro/outro detection after a library scan completes.
    /// </summary>
    public bool EnableIntroDetectSubscriber { get; set; } = true;

    /// <summary>
    /// Opt-in (default OFF): when true, PlanStage routes eligible plans through
    /// the GPU-resident decode+scale path (-hwaccel decode + scale_cuda/scale_qsv
    /// kept in GPU memory) on Nvidia/Intel hardware. Dark by default — this
    /// changes GPU runtime behaviour and must be validated on real hardware
    /// before being enabled in a release.
    /// </summary>
    public bool EnableGpuResident { get; set; }

    /// <summary>
    /// When true (default), crop detection runs as part of the PlanStage
    /// (see <c>CropDetectSubscriber</c> for details — the live wiring lives
    /// inside the pipeline, not as a separate post-process event).
    /// </summary>
    public bool EnableCropDetectSubscriber { get; set; } = true;

    /// <summary>
    /// Blu-ray / AACS / BD+ specific overrides.
    /// All fields are optional — the bundled nomercy-ffmpeg defaults apply when unset.
    /// </summary>
    public BluRayOptions? BluRay { get; set; }

    public byte[] GetDistributedEncodingSigningKey()
    {
        if (string.IsNullOrWhiteSpace(DistributedEncodingSigningKey))
            throw new InvalidOperationException(
                "DistributedEncodingSigningKey not configured — set it via AddNoMercyEncoder() to enable remote workers."
            );
        return Encoding.UTF8.GetBytes(DistributedEncodingSigningKey);
    }

    /// <summary>
    /// Coordinator URL this worker should register with on boot. When set,
    /// the self-registration hosted service POSTs to
    /// <c>{CoordinatorUrl}/api/v1/distribution/workers/register</c> on startup
    /// and sends heartbeats every
    /// <see cref="WorkerHeartbeatInterval"/>. Null = this process is a
    /// coordinator (or a standalone server) and shouldn't self-register.
    /// </summary>
    public string? CoordinatorUrl { get; set; }

    /// <summary>
    /// The base URL remote coordinators can reach THIS worker on. Must be
    /// set when <see cref="CoordinatorUrl"/> is set — the coordinator
    /// records this URL and POSTs encode tasks to it. Loopback only works
    /// for same-host-coordinator setups.
    /// </summary>
    public string? WorkerSelfBaseUrl { get; set; }

    /// <summary>
    /// Identifier this worker registers itself under. Defaults to the
    /// machine name so multi-box setups get stable identities across
    /// restarts without manual config.
    /// </summary>
    public string WorkerId { get; set; } = Environment.MachineName;

    /// <summary>
    /// How often the self-registration service heartbeats the coordinator.
    /// Must be noticeably less than the registry's stale threshold (60s
    /// default) so transient network blips don't evict the worker.
    /// </summary>
    public TimeSpan WorkerHeartbeatInterval { get; set; } = TimeSpan.FromSeconds(20);
}

/// <summary>
/// Blu-ray decryption path overrides. All fields are optional — when unset,
/// libaacs and libbdplus fall back to their standard search paths and the
/// KEYDB baked into nomercy-ffmpeg at build time.
///
/// <para>
/// Environment variable mapping (forwarded to the ffmpeg child process):
/// <list type="bullet">
///   <item><term>LIBAACS_KEY_DB</term><description>Absolute path to a KEYDB.cfg file.</description></item>
///   <item><term>LIBBDPLUS_DATABASE</term><description>Absolute path to the BD+ converter database directory.</description></item>
/// </list>
/// Variable names sourced from the libaacs source (src/libaacs/aacs.c) and
/// libbdplus source (src/libbdplus/bdplus.c) — assumed correct; update here
/// if a future libaacs/libbdplus release changes them.
/// </para>
/// </summary>
public sealed record BluRayOptions
{
    /// <summary>
    /// Absolute path to a KEYDB.cfg file. When set, the encoder forwards
    /// this as <c>LIBAACS_KEY_DB</c> to every ffmpeg/ffprobe child process
    /// that opens a bluray: URL. Leave null to use the bundled KEYDB.
    /// </summary>
    public string? KeyDbOverridePath { get; init; }

    /// <summary>
    /// Absolute path to the BD+ converter database directory. When set,
    /// forwarded as <c>LIBBDPLUS_DATABASE</c> to child processes. Leave null
    /// to use the database bundled with nomercy-ffmpeg.
    /// </summary>
    public string? AacsKeysOverridePath { get; init; }
}
