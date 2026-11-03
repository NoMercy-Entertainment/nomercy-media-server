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
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using NoMercy.Encoder.Errors;

namespace NoMercy.Encoder.Hardware;

/// <summary>
/// Cross-platform <see cref="IResourceMonitor"/> that reads CPU and memory
/// metrics from the host process + <see cref="System.Diagnostics"/>. GPU
/// utilization stays at 0 — reading it reliably needs a vendor-specific
/// tool (nvidia-smi, rocm-smi, intel_gpu_top) that ships with drivers but
/// not the .NET runtime. <see cref="NvmlGpuSampler"/>, the registered
/// monitor, overrides the GPU members with nvidia-smi readings.
/// </summary>
public class ProcessResourceMonitor : IResourceMonitor
{
    private DateTime _lastSnapshotAt = DateTime.UtcNow;
    private TimeSpan _lastCpuTime = TimeSpan.Zero;
    private readonly Lock _snapshotLock = new();

    private long _lastSystemIdle;
    private long _lastSystemKernel;
    private long _lastSystemUser;
    private long _lastLinuxIdle;
    private long _lastLinuxTotal;
    private DateTime _lastSystemSampleAt = DateTime.MinValue;
    private double _lastSystemCpuPercent;

    // Separate from _lastCpuTime (GetCpuUsagePercent's baseline, guarded by
    // _snapshotLock): SampleProcessFamilyCpu used to read/write that SAME field
    // under _systemSnapshotLock instead, so the two samplers clobbered each
    // other's baseline whenever both were called (e.g. GetSystemCpuUsagePercent
    // falling back to the process-family sampler on macOS while something else
    // still polls GetCpuUsagePercent).
    private TimeSpan _lastProcessFamilyCpuTime = TimeSpan.Zero;
    private readonly Lock _systemSnapshotLock = new();

    private readonly ILogger<ProcessResourceMonitor>? _logger;
    private bool _gpuWarningLogged;
    private bool _systemSamplerWarningLogged;

    public ProcessResourceMonitor(ILogger<ProcessResourceMonitor>? logger = null)
    {
        _logger = logger;
    }

    public double GetCpuUsagePercent()
    {
        using Process current = Process.GetCurrentProcess();
        TimeSpan cpuTime = current.TotalProcessorTime;
        DateTime now = DateTime.UtcNow;

        lock (_snapshotLock)
        {
            double elapsedMs = (now - _lastSnapshotAt).TotalMilliseconds;
            double cpuMs = (cpuTime - _lastCpuTime).TotalMilliseconds;

            _lastSnapshotAt = now;
            _lastCpuTime = cpuTime;

            if (elapsedMs < 1)
                return 0;

            int cores = Math.Max(1, Environment.ProcessorCount);
            double rawPercent = (cpuMs / elapsedMs) / cores * 100.0;
            return Math.Clamp(rawPercent, 0, 100);
        }
    }

    /// <summary>
    /// System-wide CPU utilization. The dispatch gate uses this — not
    /// <see cref="GetCpuUsagePercent"/> — because ffmpeg child processes
    /// don't accrue against the server's own TotalProcessorTime and would
    /// otherwise be invisible to the budget.
    /// </summary>
    /// <remarks>
    /// Two consecutive samples are needed to compute a delta. The first call
    /// returns 0 and primes the snapshot; every subsequent call returns the
    /// rolling percentage since the previous call. Same lock-protected
    /// snapshot pattern as <see cref="GetCpuUsagePercent"/>.
    /// </remarks>
    public double GetSystemCpuUsagePercent()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return SampleWindowsSystemCpu();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return SampleLinuxSystemCpu();

        // macOS + unknown: no portable API without P/Invoke into Mach
        // host_statistics64. Fall back to the process-family sampler so the
        // gate still has a non-zero signal under sustained encode load.
        return SampleProcessFamilyCpu();
    }

    /// <inheritdoc />
    /// <remarks>Always 0 here; <see cref="NvmlGpuSampler"/> measures NVIDIA encoders.</remarks>
    public virtual double GetGpuEncodeUtilization(string gpuDeviceKey) => 0.0;

    /// <inheritdoc />
    /// <remarks>
    /// Always returns an empty list — GPU telemetry requires a vendor-specific
    /// sampler such as <c>NvmlGpuSampler</c>. The unsupported warning is logged
    /// once at startup (rule <c>hardware.gpu_telemetry_unsupported</c>).
    /// </remarks>
    public virtual Task<IReadOnlyList<GpuProcessSample>> SampleGpuAsync(
        CancellationToken cancellationToken = default
    )
    {
        if (!_gpuWarningLogged)
        {
            _gpuWarningLogged = true;
            _logger?.LogWarning(
                "[{RuleId}] GPU process telemetry is not available on this platform. "
                    + "Install a vendor-specific sampler (e.g. NvmlGpuSampler) to enable live GPU utilization.",
                EncoderRuleId.HardwareGpuTelemetryUnsupported
            );
        }

        return Task.FromResult<IReadOnlyList<GpuProcessSample>>([]);
    }

    public long GetAvailableMemoryMb()
    {
        GCMemoryInfo info = GC.GetGCMemoryInfo();
        long available = info.TotalAvailableMemoryBytes - info.MemoryLoadBytes;
        return Math.Max(0, available / (1024 * 1024));
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(
        out long lpIdleTime,
        out long lpKernelTime,
        out long lpUserTime
    );

    private double SampleWindowsSystemCpu()
    {
        try
        {
            if (!GetSystemTimes(out long idle, out long kernel, out long user))
            {
                WarnSystemSamplerFailed("GetSystemTimes returned false");
                return SampleProcessFamilyCpu();
            }

            lock (_systemSnapshotLock)
            {
                long idleDelta = idle - _lastSystemIdle;
                long kernelDelta = kernel - _lastSystemKernel;
                long userDelta = user - _lastSystemUser;

                _lastSystemIdle = idle;
                _lastSystemKernel = kernel;
                _lastSystemUser = user;

                // First call primes the snapshot.
                long totalDelta = kernelDelta + userDelta;
                if (totalDelta <= 0 || _lastSystemSampleAt == DateTime.MinValue)
                {
                    _lastSystemSampleAt = DateTime.UtcNow;
                    return _lastSystemCpuPercent;
                }

                // kernel time on Windows INCLUDES idle time, so subtract
                // idle to isolate actual busy ticks.
                double busy = totalDelta - idleDelta;
                double percent = Math.Clamp(busy / totalDelta * 100.0, 0, 100);
                _lastSystemCpuPercent = percent;
                _lastSystemSampleAt = DateTime.UtcNow;
                return percent;
            }
        }
        catch (Exception ex)
        {
            WarnSystemSamplerFailed($"Windows GetSystemTimes threw: {ex.Message}");
            return SampleProcessFamilyCpu();
        }
    }

    private double SampleLinuxSystemCpu()
    {
        try
        {
            // /proc/stat first line: "cpu  user nice system idle iowait irq softirq steal ..."
            string firstLine = File.ReadAllLines("/proc/stat")[0];
            string[] parts = firstLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            // parts[0] == "cpu", parts[1..] are tick counts.
            if (parts.Length < 5)
            {
                WarnSystemSamplerFailed("/proc/stat first line malformed");
                return SampleProcessFamilyCpu();
            }

            long user = long.Parse(parts[1]);
            long nice = long.Parse(parts[2]);
            long system = long.Parse(parts[3]);
            long idle = long.Parse(parts[4]);
            long iowait = parts.Length > 5 ? long.Parse(parts[5]) : 0;
            long irq = parts.Length > 6 ? long.Parse(parts[6]) : 0;
            long softirq = parts.Length > 7 ? long.Parse(parts[7]) : 0;
            long steal = parts.Length > 8 ? long.Parse(parts[8]) : 0;

            long idleAll = idle + iowait;
            long busy = user + nice + system + irq + softirq + steal;
            long total = idleAll + busy;

            lock (_systemSnapshotLock)
            {
                long idleDelta = idleAll - _lastLinuxIdle;
                long totalDelta = total - _lastLinuxTotal;

                _lastLinuxIdle = idleAll;
                _lastLinuxTotal = total;

                if (totalDelta <= 0 || _lastSystemSampleAt == DateTime.MinValue)
                {
                    _lastSystemSampleAt = DateTime.UtcNow;
                    return _lastSystemCpuPercent;
                }

                double percent = Math.Clamp((1.0 - (double)idleDelta / totalDelta) * 100.0, 0, 100);
                _lastSystemCpuPercent = percent;
                _lastSystemSampleAt = DateTime.UtcNow;
                return percent;
            }
        }
        catch (Exception ex)
        {
            WarnSystemSamplerFailed($"/proc/stat read failed: {ex.Message}");
            return SampleProcessFamilyCpu();
        }
    }

    /// <summary>
    /// Cross-platform fallback that sums CPU time across the server process
    /// and every running ffmpeg child. Catches the encoder load even when
    /// the OS-specific sampler is unavailable.
    /// </summary>
    internal double SampleProcessFamilyCpu()
    {
        try
        {
            TimeSpan totalCpu = TimeSpan.Zero;
            using (Process self = Process.GetCurrentProcess())
            {
                totalCpu += self.TotalProcessorTime;
            }

            foreach (Process ffmpeg in Process.GetProcessesByName("ffmpeg"))
            {
                try
                {
                    totalCpu += ffmpeg.TotalProcessorTime;
                }
                catch
                {
                    // Process may have exited between enumeration and read.
                }
                finally
                {
                    ffmpeg.Dispose();
                }
            }

            DateTime now = DateTime.UtcNow;
            lock (_systemSnapshotLock)
            {
                double elapsedMs = (now - _lastSystemSampleAt).TotalMilliseconds;
                double cpuMs = (totalCpu - _lastProcessFamilyCpuTime).TotalMilliseconds;

                if (_lastSystemSampleAt == DateTime.MinValue || elapsedMs < 1)
                {
                    _lastSystemSampleAt = now;
                    _lastProcessFamilyCpuTime = totalCpu;
                    return _lastSystemCpuPercent;
                }

                _lastSystemSampleAt = now;
                _lastProcessFamilyCpuTime = totalCpu;

                int cores = Math.Max(1, Environment.ProcessorCount);
                double percent = Math.Clamp(cpuMs / elapsedMs / cores * 100.0, 0, 100);
                _lastSystemCpuPercent = percent;
                return percent;
            }
        }
        catch
        {
            return 0;
        }
    }

    private void WarnSystemSamplerFailed(string detail)
    {
        if (_systemSamplerWarningLogged)
            return;
        _systemSamplerWarningLogged = true;
        _logger?.LogWarning(
            "System-wide CPU sampler unavailable ({Detail}); falling back to process-family sampler. "
                + "Encoder dispatch will still throttle on encoder load but won't react to unrelated host activity.",
            detail
        );
    }
}

/// <summary>
/// Zero-valued monitor for installs that don't care about live resource
/// metrics (e.g. tests or CLI-only runs).
/// </summary>
public sealed class NullResourceMonitor : IResourceMonitor
{
    public double GetCpuUsagePercent() => 0;

    public double GetSystemCpuUsagePercent() => 0;

    public double GetGpuEncodeUtilization(string gpuDeviceKey) => 0;

    public long GetAvailableMemoryMb() => 0;

    public Task<IReadOnlyList<GpuProcessSample>> SampleGpuAsync(
        CancellationToken cancellationToken = default
    ) => Task.FromResult<IReadOnlyList<GpuProcessSample>>([]);
}
