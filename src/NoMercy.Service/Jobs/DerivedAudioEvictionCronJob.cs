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

using NoMercy.MediaProcessing.DerivedAudio;
using NoMercy.NmSystem.Configuration;
using NoMercyQueue.Core;
using NoMercyQueue.Core.Interfaces;

namespace NoMercy.Service.Jobs;

/// <summary>
/// Keeps the content-addressed derived-audio store (rendered stems and
/// transitions) under the dashboard-configured size cap. Runs hourly,
/// evicting the oldest entries by <c>LastUsedAt</c> — never one still inside
/// the store's own grace window — until the store is back under
/// <see cref="RuntimeServerSettings.DerivedAudioCapBytes" />.
/// </summary>
public class DerivedAudioEvictionCronJob : ICronJobExecutor
{
    private readonly IDerivedAudioStore _store;

    public string CronExpression => new CronExpressionBuilder().Hourly();
    public string JobName => "Derived Audio Eviction";

    public DerivedAudioEvictionCronJob(IDerivedAudioStore store)
    {
        _store = store;
    }

    public async Task ExecuteAsync(string parameters, CancellationToken cancellationToken = default)
    {
        await _store.EvictAsync(
            RuntimeServerSettings.Current.DerivedAudioCapBytes,
            TimeSpan.FromHours(24),
            cancellationToken
        );
    }
}
