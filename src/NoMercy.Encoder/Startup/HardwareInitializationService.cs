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

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NoMercy.Encoder.Hardware;
using NoMercy.NmSystem.Lifecycle;

namespace NoMercy.Encoder.Startup;

public class HardwareInitializationService(
    IHardwareDetector hardwareDetector,
    FfmpegCapabilities ffmpegCapabilities,
    IHardwareEncoderProbe hardwareEncoderProbe,
    IDriverChangeDetector driverChangeDetector,
    IBenchmarkJobTracker benchmarkJobTracker,
    HardwareCapabilitiesHolder capabilitiesHolder,
    ILogger<HardwareInitializationService> logger,
    IServerPhaseTracker? phaseTracker = null,
    int probeRetryDelayMs = 2_000
) : IHostedService
{
    // Maximum number of times to retry a probe that returns an empty encoder
    // list — guards against transient binary-not-yet-ready races without
    // permanently stranding GPU encode jobs on hosts that do have NVENC/AMF/QSV.
    private const int MaxProbeRetries = 5;

    public bool IsReady { get; private set; }

    public IHardwareCapabilities? Capabilities
    {
        get => capabilitiesHolder.Current;
        private set => capabilitiesHolder.Current = value;
    }

    /// <summary>
    /// Exposed for tests (via InternalsVisibleTo) so test code can await the
    /// background detection task rather than polling <see cref="IsReady"/>.
    /// Not part of the public API.
    /// </summary>
    internal Task DetectionTask { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Returns immediately so the hosted-service start pipeline is never
    /// blocked. Hardware detection runs on a background task and gates on
    /// <see cref="BootStage.Binaries"/> — the only real dependency of
    /// <c>ffmpeg -encoders</c> is ffmpeg itself being on disk. Auth, Network
    /// and Registered (rolled up in <see cref="BootStage.All"/>) are about
    /// this server's relationship with nomercy-tv and are irrelevant to
    /// spawning a local ffmpeg process; waiting on them only delayed GPU
    /// detection, and transitively the encoder queues, behind unrelated
    /// setup work.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // The token handed to StartAsync signals a failed STARTUP, not shutdown, so on
        // its own it never fires when the host is stopped. The probe below waits on
        // BootStage.Binaries, which is process-wide and deliberately survives the
        // HTTP→HTTPS restart, so the setup host's copy of this service would sit
        // waiting through its own disposal and then run against a dead container: the
        // driver fingerprint could be neither read nor written, and the multi-minute
        // hardware benchmark re-ran on every boot instead of reusing the cached one.
        CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _stopping.Token
        );

        DetectionTask = Task.Run(
            async () =>
            {
                try
                {
                    await RunDetectionAsync(linked.Token);
                }
                catch (OperationCanceledException) { }
                finally
                {
                    linked.Dispose();
                }
            },
            linked.Token
        );

        return Task.CompletedTask;
    }

    private readonly CancellationTokenSource _stopping = new();

    private async Task RunDetectionAsync(CancellationToken ct)
    {
        // Wait until ffmpeg is confirmed on disk. Without this gate the probe races
        // provisioning and can see a missing or partially-written binary, causing
        // ffmpeg -encoders to fail and the GPU codec list to be empty for the entire
        // server lifetime.
        if (phaseTracker is not null)
        {
            logger.LogInformation(
                "Hardware detection waiting for BootStage.Binaries (ffmpeg on disk)..."
            );
            await phaseTracker.WhenReachedAsync(BootStage.Binaries, ct).ConfigureAwait(false);
            logger.LogInformation("BootStage.Binaries reached — starting hardware probe");
        }

        if (ct.IsCancellationRequested)
            return;

        logger.LogInformation("Starting hardware detection...");

        try
        {
            // Probe FFmpeg capabilities FIRST — GPU detection needs HasEncoder() to be populated.
            // Retry on an empty encoder result: a transient probe failure (locked or
            // still-being-replaced binary) returns an empty set that downstream would
            // misread as "software-only host". After MaxProbeRetries the server falls
            // back to CPU-only so a genuinely GPU-less host still works.
            await ProbeWithRetryAsync(ct).ConfigureAwait(false);

            IReadOnlyList<GpuDevice> gpus = await hardwareDetector
                .DetectGpusAsync(ct)
                .ConfigureAwait(false);
            int cpuCores = await hardwareDetector.DetectCpuCoreCountAsync(ct).ConfigureAwait(false);

            // No GPU of any vendor was detected, so every hardware-encoder init
            // probe would only spawn ffmpeg to fail on a missing device/driver.
            // DetectGpusAsync already covers the one case vendor detection can
            // miss — NVIDIA in a container without /dev/dri, found via
            // nvidia-smi — so an empty result here is a genuine software-only
            // host. Skip the probe rather than logging a wall of expected init
            // failures for encoders that cannot possibly run.
            IReadOnlySet<string> usableHardwareEncoders;
            if (gpus.Count == 0)
            {
                logger.LogDebug(
                    "No GPU detected — skipping hardware-encoder init probe (software-only host)"
                );
                usableHardwareEncoders = new HashSet<string>();
            }
            else
            {
                usableHardwareEncoders = await ProbeUsableHardwareEncodersAsync(ct)
                    .ConfigureAwait(false);
            }

            Capabilities = new HardwareCapabilities(gpus, cpuCores, usableHardwareEncoders);
            IsReady = true;

            System.Text.StringBuilder summary = new();
            summary.Append("Hardware detection complete - encoder ready:");
            summary.Append(
                $"\n  FFmpeg : {ffmpegCapabilities.AvailableEncoders.Count} encoders, "
                    + $"{ffmpegCapabilities.AvailableFilters.Count} filters"
            );
            summary.Append($"\n  CPU    : {cpuCores} cores");
            if (gpus.Count == 0)
                summary.Append("\n  GPU    : none (software-only)");
            else
                foreach (GpuDevice gpu in gpus)
                    summary.Append(
                        $"\n  GPU    : {gpu.Vendor} {gpu.Name} "
                            + $"({gpu.VramMb}MB VRAM, max {gpu.MaxEncoderSessions} sessions)"
                    );
            summary.Append(
                usableHardwareEncoders.Count == 0
                    ? "\n  HW init probe : no hardware encoders usable (software-only)"
                    : $"\n  HW init probe : usable [{string.Join(", ", usableHardwareEncoders)}]"
            );
            logger.LogInformation("{HardwareSummary}", summary.ToString());
        }
        catch (Exception ex)
            when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Hardware detection failed — software-only fallback");
            Capabilities = new HardwareCapabilities(Gpus: [], CpuCores: Environment.ProcessorCount);
            IsReady = true;
        }

        // Driver change detection — runs in its own try-catch so a failure
        // here (fingerprint store unavailable, mock-of in tests, etc.) cannot
        // revert the Capabilities we just established. First boot:
        // HardwareBenchmarkBackgroundService handles initial calibration;
        // subsequent boots with a changed driver queue an immediate recalibration.
        try
        {
            DriverChangeResult driverResult = await driverChangeDetector
                .DetectAndPersistAsync(ct)
                .ConfigureAwait(false);

            if (driverResult.IsFirstBoot)
            {
                logger.LogInformation(
                    "Driver fingerprint: first boot (hash {Hash}) — initial calibration deferred to benchmark service",
                    driverResult.CurrentHash
                );
            }
            else if (driverResult.Changed)
            {
                logger.LogWarning(
                    "GPU driver change detected (prev={Prev}, curr={Curr}) — queuing benchmark recalibration",
                    driverResult.PreviousHash,
                    driverResult.CurrentHash
                );
                benchmarkJobTracker.Start([], []);
            }
            else
            {
                logger.LogInformation(
                    "Driver fingerprint unchanged (hash {Hash})",
                    driverResult.CurrentHash
                );
            }
        }
        catch (Exception ex)
            when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(
                ex,
                "Driver fingerprint check failed — continuing without recalibration trigger"
            );
        }

        phaseTracker?.MarkComplete(BootStage.Hardware);
    }

    /// <summary>
    /// Runs the real hardware-encoder init probe once, against every
    /// compiled-in hardware encoder name ffmpeg advertises — not just the
    /// ones matching a physically detected GPU vendor. This is deliberate:
    /// the probe result IS the authority PlanStage uses for selection, so it
    /// must independently confirm or refute every candidate rather than only
    /// checking the ones vendor detection already believes are present. A
    /// probe failure degrades to "no hardware encoders usable" (software-only)
    /// instead of throwing — an init-probe outage must never block boot or
    /// crash the server, it only removes hardware acceleration for this run.
    ///
    /// The caller only reaches this method when at least one GPU was detected;
    /// a zero-GPU host is short-circuited before here so no ffmpeg process is
    /// spawned for encoders that have no device to run on.
    /// </summary>
    private async Task<IReadOnlySet<string>> ProbeUsableHardwareEncodersAsync(CancellationToken ct)
    {
        try
        {
            IReadOnlyList<string> candidates =
            [
                .. ffmpegCapabilities.AvailableEncoders.Where(encoderName =>
                    GpuEncoderTokens.VendorForEncoderName(encoderName) is not null
                ),
            ];

            if (candidates.Count == 0)
                return new HashSet<string>();

            return await hardwareEncoderProbe.ProbeAsync(candidates, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
            when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(
                ex,
                "Hardware encoder init probe failed — no hardware encoders usable (software-only)"
            );
            return new HashSet<string>();
        }
    }

    /// <summary>
    /// Runs <see cref="FfmpegCapabilities.ProbeAsync"/> and retries up to
    /// <see cref="MaxProbeRetries"/> times when the encoder list comes back
    /// empty — which happens when the binary is still being written to disk.
    /// Throws on the final attempt so the caller's catch block handles the
    /// CPU-only fallback.
    /// </summary>
    private async Task ProbeWithRetryAsync(CancellationToken ct)
    {
        for (int attempt = 0; attempt <= MaxProbeRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                await ffmpegCapabilities.ProbeAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
                when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                if (attempt == MaxProbeRetries)
                    throw;

                logger.LogWarning(
                    ex,
                    "ffmpeg probe failed (attempt {Attempt}/{Max}), retrying in {DelayMs}ms",
                    attempt + 1,
                    MaxProbeRetries,
                    probeRetryDelayMs
                );
                await Task.Delay(probeRetryDelayMs, ct).ConfigureAwait(false);
                continue;
            }

            if (ffmpegCapabilities.AvailableEncoders.Count > 0)
                return;

            if (attempt == MaxProbeRetries)
            {
                logger.LogWarning(
                    "ffmpeg -encoders returned empty set after {Max} attempt(s) — proceeding with CPU-only capabilities",
                    MaxProbeRetries + 1
                );
                return;
            }

            logger.LogWarning(
                "ffmpeg -encoders returned empty set (attempt {Attempt}/{Max}), retrying in {DelayMs}ms",
                attempt + 1,
                MaxProbeRetries,
                probeRetryDelayMs
            );
            await Task.Delay(probeRetryDelayMs, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Cancels the deferred probe. Without this the detection task outlives the host
    /// that owns the container it resolves its database context from.
    /// </summary>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping.Cancel();
        return Task.CompletedTask;
    }
}
