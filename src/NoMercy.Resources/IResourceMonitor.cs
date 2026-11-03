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

namespace NoMercy.Resources;

/// <summary>
/// Per-process GPU sample emitted by <see cref="IResourceMonitor.SampleGpu"/>.
/// When vendor-specific telemetry is unavailable the list is empty — callers
/// must treat a missing entry as "unknown", not as "idle".
/// </summary>
public sealed record GpuProcessSample(
    int Pid,
    int GpuIndex,
    int EncoderUtilizationPercent,
    long EncoderMemoryBytes
);

/// <summary>
/// Provides live resource telemetry to <see cref="IResourceBudget"/> and to
/// the hardware benchmark. GPU devices are identified by key string (matching
/// <c>GpuDevice.Name</c>) so this interface carries no encoder dependency.
/// </summary>
public interface IResourceMonitor
{
    double GetCpuUsagePercent();

    /// <summary>
    /// System-wide CPU utilization across every process on the host (not just
    /// the server). This is the signal the dispatch gate consults before
    /// granting a new encoder lease — <see cref="GetCpuUsagePercent"/> only
    /// counts the server process itself, so ffmpeg child processes are
    /// invisible to it and the budget never sees the encoder load it caused.
    /// </summary>
    double GetSystemCpuUsagePercent();

    /// <summary>
    /// Current hardware-encoder load of the GPU behind <paramref name="gpuDeviceKey"/>,
    /// in percent (0-100) like every other signal here, not a 0-1 fraction. The
    /// dispatch gate compares it as-is against <c>ResourceBudgetOptions.GpuHeadroomPercent</c>.
    /// Returns 0 when the load is unknown.
    /// </summary>
    double GetGpuEncodeUtilization(string gpuDeviceKey);

    long GetAvailableMemoryMb();

    /// <summary>
    /// Returns per-process GPU encoder utilization for every ffmpeg process the
    /// vendor runtime can observe. Returns an empty list on platforms or
    /// configurations where GPU telemetry is unavailable.
    /// </summary>
    Task<IReadOnlyList<GpuProcessSample>> SampleGpuAsync(
        CancellationToken cancellationToken = default
    );
}
