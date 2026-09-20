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
/// Named work the host runs and supervises.
/// <para>
/// One cron per plugin did not fit. The torrent plugin has four jobs and
/// dispatched them by name inside one handler, and its DHT wants to run for as
/// long as the server does rather than on a schedule at all.
/// </para>
/// </summary>
public interface IPluginScheduler
{
    void Register(PluginScheduledJob job);

    Task<JobId> RunOnceAsync(string name, DateTimeOffset when, CancellationToken ct = default);

    /// <summary>
    /// Queues it and answers at once. The torrent plugin awaited a 29-minute
    /// cycle inside an HTTP request, which held a request thread for the whole
    /// cycle and timed out every caller that asked.
    /// </summary>
    Task<JobId> RunNowAsync(string name, CancellationToken ct = default);

    void StartWorker(PluginWorker worker);

    Task StopWorkerAsync(string name, CancellationToken ct = default);

    Task<PluginWorkerState> WorkerStateAsync(string name, CancellationToken ct = default);
}
