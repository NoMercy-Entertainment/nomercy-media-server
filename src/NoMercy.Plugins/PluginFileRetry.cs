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

namespace NoMercy.Plugins;

/// <summary>
/// Retries a filesystem operation while it fails on a file a just-unloaded
/// assembly context still holds.
/// <para>
/// Polls rather than forces a collection: <c>GC.Collect</c> stops every
/// thread, which on a media server means playback stutters, and
/// <see cref="IPluginAssemblyTracker"/> already established that a lock is
/// something to wait out, not force. Shared by every lifecycle operation that
/// can race a just-unloaded assembly - update's swap and rollback, uninstall's
/// directory delete - so the wait/backoff policy lives in one place rather
/// than being re-tuned per caller.
/// </para>
/// </summary>
internal static class PluginFileRetry
{
    public static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Runs <paramref name="operation"/>, retrying with backoff while it throws
    /// <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/>,
    /// until <paramref name="budget"/> (or <see cref="DefaultBudget"/>) runs
    /// out. Returns false, having made no further attempt, once the budget is
    /// spent - the caller decides what "still locked" means for it.
    /// </summary>
    public static async Task<bool> TryAsync(
        Action operation,
        CancellationToken ct,
        TimeSpan? budget = null
    )
    {
        DateTime deadline = DateTime.UtcNow + (budget ?? DefaultBudget);
        TimeSpan delay = TimeSpan.FromMilliseconds(50);

        while (true)
        {
            try
            {
                operation();
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (DateTime.UtcNow >= deadline)
                {
                    return false;
                }

                await Task.Delay(delay, ct);
                delay =
                    delay * 2 < TimeSpan.FromMilliseconds(500)
                        ? delay * 2
                        : TimeSpan.FromMilliseconds(500);
            }
        }
    }
}
