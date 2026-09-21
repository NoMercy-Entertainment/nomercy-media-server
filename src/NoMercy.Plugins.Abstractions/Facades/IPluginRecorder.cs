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

namespace NoMercy.Plugins.Abstractions;

/// <summary>
/// Recordings the host owns: scheduled from the guide, resumed after a restart,
/// retained by policy and counted against the plugin's disk quota.
/// </summary>
public interface IPluginRecorder
{
    Task<JobId> ScheduleAsync(PluginRecordingRequest request, CancellationToken ct = default);

    Task CancelAsync(JobId recording, CancellationToken ct = default);

    Task<IReadOnlyList<PluginRecording>> ListAsync(CancellationToken ct = default);
}
