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

using System.Globalization;
using Microsoft.Extensions.Logging;
using NoMercy.Encoder.Infrastructure;

namespace NoMercy.Encoder.Hardware;

/// <summary>
/// <see cref="ProcessResourceMonitor"/> that adds NVIDIA telemetry through
/// nvidia-smi: per-process GPU memory (<see cref="SampleGpuAsync"/>) and the
/// device encoder load (<see cref="GetGpuEncodeUtilization"/>) that the GPU
/// headroom gate in <see cref="ResourceBudget"/> compares against
/// EncoderGpuHeadroomPercent. Reads empty samples and 0 % when nvidia-smi is
/// absent or fails.
/// </summary>
public sealed class NvmlGpuSampler : ProcessResourceMonitor
{
    private static readonly TimeSpan MinSampleInterval = TimeSpan.FromSeconds(2);

    private static readonly string[] EncoderUtilizationQuery =
    [
        "--query-gpu=utilization.encoder",
        "--format=csv,noheader,nounits",
    ];

    private readonly IProcessRunner _processRunner;
    private readonly ILogger<NvmlGpuSampler> _logger;
    private readonly IHardwareCapabilities? _hardware;
    private readonly SemaphoreSlim _sampleLock = new(1, 1);
    private DateTime _lastSampleAt = DateTime.MinValue;
    private IReadOnlyList<GpuProcessSample> _lastSamples = [];

    private double _encoderUtilizationPercent;
    private long _encoderSampledAtTicks = DateTime.MinValue.Ticks;
    private int _encoderRefreshRunning;

    public NvmlGpuSampler(
        IProcessRunner processRunner,
        ILogger<NvmlGpuSampler> logger,
        IHardwareCapabilities? hardware = null
    )
        : base(null)
    {
        _processRunner = processRunner;
        _logger = logger;
        _hardware = hardware;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Returns the busiest NVIDIA encoder, in percent, for an NVIDIA key and 0
    /// for any other vendor. The busiest one because <see cref="ResourceBudget"/>
    /// pools same-vendor GPUs behind one slot pool. The reading is cached and
    /// refreshed in the background once it is older than two seconds, so a
    /// lease request never waits on nvidia-smi. Until the first refresh lands it
    /// reads 0; the running-lease estimate in the gate covers that gap.
    /// </remarks>
    public override double GetGpuEncodeUtilization(string gpuDeviceKey)
    {
        if (!IsNvidiaKey(gpuDeviceKey))
            return 0;

        DateTime sampledAt = new(Interlocked.Read(ref _encoderSampledAtTicks), DateTimeKind.Utc);
        bool stale = DateTime.UtcNow - sampledAt >= MinSampleInterval;
        if (stale && Interlocked.CompareExchange(ref _encoderRefreshRunning, 1, 0) == 0)
            _ = Task.Run(RefreshEncoderUtilizationAsync);

        return Volatile.Read(ref _encoderUtilizationPercent);
    }

    public override async Task<IReadOnlyList<GpuProcessSample>> SampleGpuAsync(
        CancellationToken cancellationToken = default
    )
    {
        await _sampleLock.WaitAsync(cancellationToken);
        try
        {
            if (DateTime.UtcNow - _lastSampleAt < MinSampleInterval)
                return _lastSamples;

            IReadOnlyList<GpuProcessSample> fresh = await RunNvidiaSmiAsync(cancellationToken);
            _lastSamples = fresh;
            _lastSampleAt = DateTime.UtcNow;
            return fresh;
        }
        finally
        {
            _sampleLock.Release();
        }
    }

    private async Task<IReadOnlyList<GpuProcessSample>> RunNvidiaSmiAsync(
        CancellationToken cancellationToken
    )
    {
        try
        {
            using CancellationTokenSource timeoutCts = new(TimeSpan.FromSeconds(5));
            using CancellationTokenSource linkedCts =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    timeoutCts.Token
                );

            ProcessResult result = await _processRunner.RunAsync(
                "nvidia-smi",
                ["--query-compute-apps=pid,used_gpu_memory", "--format=csv,noheader,nounits"],
                workingDirectory: null,
                cancellationToken: linkedCts.Token
            );

            if (!result.IsSuccess || string.IsNullOrWhiteSpace(result.StdOut))
                return [];

            return ParseNvidiaSmiOutput(result.StdOut);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("nvidia-smi timed out — returning empty GPU sample");
            return [];
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "nvidia-smi unavailable or failed — GPU utilization will read 0");
            return [];
        }
    }

    private async Task RefreshEncoderUtilizationAsync()
    {
        try
        {
            using CancellationTokenSource timeoutCts = new(TimeSpan.FromSeconds(5));
            ProcessResult result = await _processRunner.RunAsync(
                "nvidia-smi",
                EncoderUtilizationQuery,
                workingDirectory: null,
                cancellationToken: timeoutCts.Token
            );

            double percent = result.IsSuccess ? ParseEncoderUtilization(result.StdOut) : 0;
            Volatile.Write(ref _encoderUtilizationPercent, percent);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "nvidia-smi encoder query failed — GPU encode load reads 0");
            Volatile.Write(ref _encoderUtilizationPercent, 0);
        }
        finally
        {
            Interlocked.Exchange(ref _encoderSampledAtTicks, DateTime.UtcNow.Ticks);
            Interlocked.Exchange(ref _encoderRefreshRunning, 0);
        }
    }

    private bool IsNvidiaKey(string gpuDeviceKey)
    {
        if (GpuEncoderTokens.VendorForEncoderName(gpuDeviceKey) == GpuVendor.Nvidia)
            return true;

        if (
            gpuDeviceKey.Equals("nvenc", StringComparison.OrdinalIgnoreCase)
            || gpuDeviceKey.Equals("cuvid", StringComparison.OrdinalIgnoreCase)
        )
            return true;

        return _hardware?.Gpus.Any(gpu =>
                gpu.Vendor == GpuVendor.Nvidia
                && gpu.Name.Equals(gpuDeviceKey, StringComparison.OrdinalIgnoreCase)
            ) == true;
    }

    /// <summary>
    /// Reads <c>--query-gpu=utilization.encoder</c> output (one percentage per
    /// GPU, "[N/A]" where unsupported) and returns the highest, or 0 when none parse.
    /// </summary>
    internal static double ParseEncoderUtilization(string stdOut)
    {
        double highest = 0;
        foreach (string line in stdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (
                double.TryParse(
                    line.Trim(),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out double percent
                )
            )
                highest = Math.Max(highest, Math.Clamp(percent, 0, 100));
        }

        return highest;
    }

    internal static IReadOnlyList<GpuProcessSample> ParseNvidiaSmiOutput(string stdOut)
    {
        List<GpuProcessSample> samples = [];

        foreach (string line in stdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmedLine = line.Trim();
            if (string.IsNullOrEmpty(trimmedLine))
                continue;

            string[] fields = trimmedLine.Split(',');
            if (fields.Length < 2)
                continue;

            if (!int.TryParse(fields[0].Trim(), out int pid))
                continue;

            if (!long.TryParse(fields[1].Trim(), out long memoryMb))
                continue;

            samples.Add(
                new(
                    Pid: pid,
                    GpuIndex: 0,
                    EncoderUtilizationPercent: 0,
                    EncoderMemoryBytes: memoryMb * 1024 * 1024
                )
            );
        }

        return samples;
    }
}
