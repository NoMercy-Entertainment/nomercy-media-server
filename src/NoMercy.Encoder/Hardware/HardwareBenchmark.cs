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

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using NoMercy.Encoder.Codecs;
using NoMercy.Encoder.Composition;
using NoMercy.Encoder.Execution;
using NoMercy.Encoder.Infrastructure;
using NoMercy.Encoder.Progress;

namespace NoMercy.Encoder.Hardware;

/// <summary>
/// Runs short calibration encodes to populate <see cref="SpeedIndex"/>.
/// For each available encoder × resolution tier × target device it spawns
/// a short synthetic source via the lavfi <c>testsrc</c> muxer and measures
/// the achieved fps from ffmpeg's structured progress output.
///
/// Hardware encoders are benchmarked once per matching GPU so a box with
/// multiple GPUs (two NVIDIA cards, iGPU + dGPU, etc.) gets a separate
/// entry per device. Software encoders run once; their device name is null.
/// Results persist via <see cref="ISpeedIndexStore"/> so subsequent server
/// starts reuse the measurements — see <see cref="NeedsRecalibration"/>.
/// </summary>
public class HardwareBenchmark(
    CodecRegistry codecRegistry,
    IHardwareCapabilities hardware,
    IProcessRunner processRunner,
    ISpeedIndexStore store,
    EncoderOptions options,
    ILogger<HardwareBenchmark> logger,
    IAnalysisProgressObserver? progress = null
) : IHardwareBenchmark
{
    // Standard benchmark tiers for every target.
    internal static readonly (int Width, int Height)[] DefaultTiers =
    [
        (1920, 1080),
        (1280, 720),
        (854, 480),
    ];

    // 4K tier — added to DefaultTiers for any encoder fast enough to sustain
    // 4K calibration in a reasonable time. Hardware threshold is "card
    // reports ≥4000 MB VRAM" rather than ≥6144 MB because Windows wmic
    // returns AdapterRAM as a uint32 that overflows at 4095 MB on 8GB+
    // cards (an RTX 2080 SUPER reports 4095 MB even though the real VRAM
    // is 8192 MB). Lower threshold passes those cards through correctly;
    // genuine low-VRAM cards (1-2 GB iGPUs) that fail OOM at 4K are
    // swallowed by the per-probe catch in CalibrateAsync.
    private const int FourKCapableVramMb = 4_000;
    private static readonly (int Width, int Height) UhdTier = (3840, 2160);

    // Software encoder names that are fast enough to be benchmarked at 4K
    // without dominating the total calibration runtime. libx264, libx265,
    // and libsvtav1 hit ~real-time at 1080p on a modern CPU and 0.5–2x at
    // 4K — a 300-frame probe lands in ≤30s. libaom-av1 / librav1e are
    // explicitly excluded: they crawl at 0.05 fps at 4K and a single tier
    // would block the benchmark for 5+ minutes.
    private static readonly HashSet<string> FastSoftwareEncoders = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "libx264",
        "libx265",
        "libsvtav1",
    };

    // Slow encoders that need the warmup-pad rule waived and a hard frame
    // cap to keep total calibration tractable. ffmpeg's progress fps at
    // these encoders is meaningful from frame 1 because there is no
    // realistic steady-state to wait for — they're already crawling.
    private static readonly HashSet<string> SlowSoftwareEncoders = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "libaom-av1",
        "librav1e",
    };

    internal const double SourceFrameRate = 30.0;

    // Source-clip length per encoder class. Fast encoders need a longer
    // source so the progress stream emits multiple non-zero fps lines and
    // the measurement reflects steady-state throughput rather than
    // first-frame spin-up time. Slow encoders keep a short source so a
    // single tier doesn't block the calibration for minutes.
    private const double FastEncoderSourceSeconds = 10.0;
    private const double SlowEncoderSourceSeconds = 2.0;

    // Per-encoder frame cap. Mirrors the source-seconds split so the cap
    // and the source agree. Fast encoders measure 300 frames (~10s @ 30fps);
    // slow ones cap at 60 frames so libaom-av1 / librav1e don't spend
    // multiple minutes on a single tier.
    //
    // Encoders ramp UP to steady-state throughput rather than down — the
    // first sample over second 1 of source captures spin-up overhead, the
    // sample over second 5 captures steady state. observedFps takes the
    // Math.Max across all samples, so the longer probe naturally selects
    // the steady-state reading without an explicit warmup-discard step.
    private const int FastEncoderMaxFrames = 300;
    private const int SlowEncoderMaxFrames = 60;

    // Recalibrate once a month by default. Hardware / driver updates can
    // change real throughput noticeably.
    private static readonly TimeSpan RecalibrationInterval = TimeSpan.FromDays(30);

    /// <summary>
    /// Version stamp baked into every cached SpeedIndex on disk. Bump when
    /// changing the calibration source-seconds, frame cap, tier list, or
    /// candidate enumeration so old cached numbers (which were measured
    /// against a different probe shape) get auto-invalidated on the next
    /// boot instead of waiting out the 30-day grace window.
    ///
    /// Version history:
    ///   1 — original 1-sec / 30-frame probe, GPU detection broken,
    ///       libx264 silently dropped, no 4K coverage.
    ///   2 — fixed IHardwareCapabilities forwarder (real GPU enumeration),
    ///       wall-clock fps fallback, 4K threshold drop, per-encoder probe
    ///       length (300/60 frames over 10s/2s source). Existing v1 caches
    ///       are CPU-only and noisy; force a recalibration so the speed
    ///       index reflects real-world throughput.
    /// </summary>
    public const int BenchmarkSchemaVersion = 2;

    private SpeedIndex _cache = new(new());
    // Calibrations are serialized: a second caller (dashboard "Run benchmark"
    // during the startup or monthly run) waits its turn instead of running a
    // second set of ffmpeg probes that would skew both measurements.
    private readonly SemaphoreSlim _runGate = new(1, 1);

    // Callers that are running or waiting. IsRunning stays true until the
    // last of them ends, so one run finishing cannot clear it for another.
    private int _activeCalls;
    private BenchmarkProgress? _progress;

    public bool IsRunning => Volatile.Read(ref _activeCalls) > 0;

    public BenchmarkProgress? CurrentProgress => _progress;

    public SpeedIndex GetCachedIndex()
    {
        if (_cache.Measurements.Count > 0)
            return _cache;

        SpeedIndex? loaded = store.Load();
        if (loaded is not null)
        {
            _cache = loaded;
            return loaded;
        }

        return _cache;
    }

    public bool NeedsRecalibration()
    {
        SpeedIndex cached = GetCachedIndex();
        if (cached.Measurements.Count == 0)
            return true;

        // Schema-version mismatch trumps the calendar grace window. Cache
        // files written by an older benchmark version measured against
        // different probe length / tier list / enumeration rules — the
        // numbers are not comparable to what the current code would emit
        // and would silently mislead encoder selection. null === pre-v2
        // (no version field at all).
        int? loadedVersion = store.LoadedSchemaVersion;
        if (loadedVersion is null || loadedVersion.Value < BenchmarkSchemaVersion)
        {
            logger.LogInformation(
                "SpeedIndex cache schema {Loaded} older than current {Current} — forcing recalibration",
                loadedVersion?.ToString() ?? "(none)",
                BenchmarkSchemaVersion
            );
            return true;
        }

        DateTime? lastCalibrated = store.LastCalibratedAt;
        if (lastCalibrated is null)
            return true;

        return DateTime.UtcNow - lastCalibrated.Value > RecalibrationInterval;
    }

    public async Task<SpeedIndex> CalibrateAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref _activeCalls);
        try
        {
            await _runGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                _progress = new(0, 0, 0, "starting", DateTime.UtcNow);
                return await CalibrateInternalAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                _runGate.Release();
            }
        }
        finally
        {
            Interlocked.Decrement(ref _activeCalls);
        }
    }

    private async Task<SpeedIndex> CalibrateInternalAsync(CancellationToken ct)
    {
        // Seed the live results dict with whatever's in the cache so probes
        // that fail this run don't make existing rows disappear from the
        // dashboard mid-benchmark. Each successful probe overwrites the
        // matching key; failed probes leave the previous reading in place
        // until the next full run.
        Dictionary<SpeedKey, SpeedMeasurement> results = new(GetCachedIndex().Measurements);

        IAnalysisProgressObserver observer = progress ?? NullAnalysisProgressObserver.Instance;
        string jobId = Guid.NewGuid().ToString("N");

        // Enumerate all (target, tier) pairs once upfront so we can compute
        // percent-complete as each probe finishes.
        List<(CalibrationTarget Target, int Width, int Height)> allWork = [];
        foreach (CalibrationTarget candidate in SelectCandidates())
        {
            foreach ((int w, int h) in TiersForTarget(candidate))
                allWork.Add((candidate, w, h));
        }

        int total = allWork.Count;
        int completed = 0;

        observer.Report(jobId, "benchmark", 0, "starting");

        foreach ((CalibrationTarget target, int tierWidth, int tierHeight) in allWork)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                SpeedMeasurement? measured = await MeasureAsync(target, tierWidth, tierHeight, ct);

                if (measured is null)
                    continue;

                SpeedKey key = new(
                    target.Codec,
                    target.Encoder.FfmpegName,
                    tierWidth,
                    target.Device?.Name
                );
                results[key] = measured;

                // Publish the partial index immediately so the dashboard's
                // GET /hardware/benchmark poll sees this row appear within
                // its next refetch interval. Without this, _cache only
                // updates after the entire ~10 min run completes and the
                // user watches a stale table for the whole benchmark.
                _cache = new(new(results));

                logger.LogInformation(
                    "Benchmarked {Encoder}{DeviceTag} @ {W}x{H}: {Fps:F1} fps ({Speed:F2}x)",
                    target.Encoder.FfmpegName,
                    target.Device is null ? " (CPU)" : $" on {target.Device.Name}",
                    tierWidth,
                    tierHeight,
                    measured.Fps,
                    measured.SpeedMultiplier
                );
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Benchmark failed for {Encoder}{DeviceTag} @ {W}x{H} — skipping",
                    target.Encoder.FfmpegName,
                    target.Device is null ? "" : $" on {target.Device.Name}",
                    tierWidth,
                    tierHeight
                );
            }
            finally
            {
                completed++;
                double pct = total > 0 ? (double)completed / total * 100.0 : 100.0;
                string stage = $"{target.Encoder.FfmpegName} {tierWidth}x{tierHeight}";
                observer.Report(jobId, "benchmark", pct, stage);
                _progress = new(pct, completed, total, stage, DateTime.UtcNow);
            }
        }

        observer.Report(jobId, "benchmark", 100, "done");
        _progress = new(100, completed, total, "done", DateTime.UtcNow);

        SpeedIndex index = new(results);
        store.Save(index);
        _cache = index;
        return index;
    }

    /// <summary>
    /// Enumerates every (codec, encoder, device) target to benchmark.
    /// Software encoders yield once with <see cref="CalibrationTarget.Device"/>
    /// = null. Hardware encoders yield once per matching GPU so a host with
    /// multiple GPUs benchmarks each card independently — the assigner can
    /// later weight work across devices of different speeds.
    /// </summary>
    internal IEnumerable<CalibrationTarget> SelectCandidates()
    {
        foreach (
            (VideoCodecType codec, EncoderInfo encoder) in codecRegistry.EnumerateVideoEncoders()
        )
        {
            if (encoder.RequiredVendor is GpuVendor vendor)
            {
                List<(GpuDevice Device, int VendorIndex)> matchingGpus = EnumerateGpusForVendor(
                    vendor
                );
                if (matchingGpus.Count == 0)
                    continue; // Vendor not installed — skip encoder entirely.

                foreach ((GpuDevice device, int vendorIndex) in matchingGpus)
                    yield return new(codec, encoder, device, vendorIndex);
            }
            else
            {
                // Software encoder — no device, no index.
                yield return new(codec, encoder, Device: null, VendorIndex: 0);
            }
        }
    }

    /// <summary>
    /// Returns the tiers to benchmark for a given target. UHD is included
    /// when either:
    ///   - the encoder runs on a GPU whose reported VRAM is ≥4000 MB
    ///     (handles wmic's int32 overflow that caps 8GB+ cards at 4095 MB), OR
    ///   - the encoder is one of the fast software encoders (libx264 /
    ///     libx265 / libsvtav1) that can sustain 4K calibration in
    ///     reasonable wall time.
    /// libaom-av1 / librav1e at 4K take 5+ minutes per probe, so they are
    /// explicitly excluded — the speed index can extrapolate from their
    /// 1080p numbers.
    /// </summary>
    internal static IEnumerable<(int Width, int Height)> TiersForTarget(CalibrationTarget target)
    {
        bool gpuFourKCapable = target.Device is { VramMb: >= FourKCapableVramMb };
        bool swFourKCapable =
            target.Device is null && FastSoftwareEncoders.Contains(target.Encoder.FfmpegName);

        if (gpuFourKCapable || swFourKCapable)
            yield return UhdTier;

        foreach ((int w, int h) in DefaultTiers)
            yield return (w, h);
    }

    /// <summary>
    /// Per-encoder source-seconds and frame-cap. Fast encoders get a 10-sec
    /// source capped at 300 frames so the steady-state throughput is what
    /// gets measured, not first-frame spin-up. Slow encoders (libaom-av1,
    /// librav1e) get a 2-sec source capped at 60 frames so a single tier
    /// doesn't block the entire calibration for multiple minutes.
    /// </summary>
    internal static (double SourceSeconds, int MaxFrames) CalibrationProfile(EncoderInfo encoder)
    {
        if (SlowSoftwareEncoders.Contains(encoder.FfmpegName))
            return (SlowEncoderSourceSeconds, SlowEncoderMaxFrames);
        return (FastEncoderSourceSeconds, FastEncoderMaxFrames);
    }

    /// <summary>
    /// Returns every GPU of the given vendor paired with its vendor-relative
    /// index (0 = first Nvidia, 1 = second Nvidia, …). The index is what
    /// ffmpeg expects in <c>cuda=cu:N</c> / <c>-gpu N</c> etc.
    /// </summary>
    private List<(GpuDevice Device, int VendorIndex)> EnumerateGpusForVendor(GpuVendor vendor)
    {
        List<(GpuDevice Device, int VendorIndex)> result = [];
        int idx = 0;
        foreach (GpuDevice gpu in hardware.Gpus)
        {
            if (gpu.Vendor != vendor)
                continue;

            result.Add((gpu, idx));
            idx++;
        }
        return result;
    }

    private async Task<SpeedMeasurement?> MeasureAsync(
        CalibrationTarget target,
        int width,
        int height,
        CancellationToken ct
    )
    {
        string[] arguments = CalibrationArgumentBuilder.BuildCalibrationArguments(
            target,
            width,
            height
        );

        ProgressParser parser = new();
        double observedFps = 0;
        int lastFrame = 0;

        void OnStdOut(string line)
        {
            FfmpegProgressSnapshot? snapshot = parser.FeedLine(line);
            if (snapshot is null)
                return;

            if (snapshot.Fps > 0)
                observedFps = Math.Max(observedFps, snapshot.Fps);
            if (snapshot.Frame > lastFrame)
                lastFrame = snapshot.Frame;
        }

        Stopwatch wall = Stopwatch.StartNew();
        ProcessResult result = await processRunner.RunAsync(
            options.FfmpegPath,
            arguments,
            OnStdOut,
            null,
            null,
            ct
        );
        wall.Stop();

        if (!result.IsSuccess)
        {
            // Log the ffmpeg stderr tail so users can see *why* an encoder
            // dropped out — commonly missing CUDA driver, no VA-API device,
            // a vendor-specific flag incompatibility, or AV1 NVENC on a
            // pre-Ada card. Information level: a missing benchmark row in
            // the dashboard is otherwise indistinguishable from "the
            // encoder doesn't exist", and silently skipping at Debug
            // hid the explanation from anyone running on the default
            // Information log level.
            logger.LogInformation(
                "Benchmark probe for {Encoder}{DeviceTag} @ {W}x{H} exited {Code} — encoder will be omitted from the speed index. Stderr tail: {StdErr}",
                target.Encoder.FfmpegName,
                target.Device is null ? "" : $" on {target.Device.Name}",
                width,
                height,
                result.ExitCode,
                FfmpegStderr.Tail(result.StdErr)
            );
            return null;
        }

        // ffmpeg's `fps=` progress field is computed over a one-second
        // sliding window — fast encoders like libx264 finish 30 frames in
        // under 100ms and never emit a non-zero fps line. Fall back to
        // wall-clock-derived fps so libx264 / nvenc / hwaccel encoders
        // actually land in the speed index instead of being silently
        // dropped. Only used when the progress stream gave us nothing
        // usable — measured fps wins when both are present because it's
        // closer to the encoder's steady-state rate (excludes process
        // spin-up time).
        if (observedFps <= 0 && lastFrame > 0 && wall.Elapsed.TotalSeconds > 0)
        {
            observedFps = lastFrame / wall.Elapsed.TotalSeconds;
            logger.LogDebug(
                "Wall-clock fps fallback for {Encoder} @ {W}x{H}: {Frames} frames in {Elapsed:F2}s = {Fps:F1} fps",
                target.Encoder.FfmpegName,
                width,
                height,
                lastFrame,
                wall.Elapsed.TotalSeconds,
                observedFps
            );
        }

        if (observedFps <= 0)
            return null;

        double multiplier = observedFps / SourceFrameRate;
        return new(Fps: observedFps, SpeedMultiplier: multiplier, MeasuredAt: DateTime.UtcNow);
    }
}

/// <summary>
/// One benchmark run target — an encoder + the specific GPU device it
/// targets (null for software encoders). <see cref="VendorIndex"/> is the
/// device's index among GPUs of the same vendor, used for ffmpeg's
/// per-vendor device selector args.
/// </summary>
public record CalibrationTarget(
    VideoCodecType Codec,
    EncoderInfo Encoder,
    GpuDevice? Device,
    int VendorIndex
);
