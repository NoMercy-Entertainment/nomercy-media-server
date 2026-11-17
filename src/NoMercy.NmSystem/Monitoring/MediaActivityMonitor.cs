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

namespace NoMercy.NmSystem.Monitoring;

/// <summary>
/// Tracks whether a user is actively streaming, so NAS-read-heavy background
/// work (library scans, imports, extras, the segment analysis) can back
/// off while playback is in progress. <see cref="Touch"/> is called once per
/// served segment/playlist/subtitle request; <see cref="IsActive"/> reports
/// whether a touch landed within <see cref="ActiveWindow"/>.
///
/// Lock-free: the last-touch timestamp is stored as ticks in a <c>long</c>
/// field, read and written via <see cref="Interlocked"/> so callers never
/// block on a lock for what is effectively a hot path (every media serve).
/// </summary>
public class MediaActivityMonitor
{
    private const long NeverTouched = 0;

    private static readonly TimeSpan ActiveWindow = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private long _lastTouchedTicks = NeverTouched;

    /// <summary>
    /// Records that a media serve just happened, resetting the idle clock.
    /// </summary>
    public void Touch()
    {
        Interlocked.Exchange(ref _lastTouchedTicks, DateTime.UtcNow.Ticks);
    }

    /// <summary>
    /// True when <see cref="Touch"/> was called within the last
    /// <see cref="ActiveWindow"/>.
    /// </summary>
    public bool IsActive
    {
        get
        {
            long lastTouchedTicks = Interlocked.Read(ref _lastTouchedTicks);
            if (lastTouchedTicks == NeverTouched)
                return false;

            DateTime lastTouched = new(lastTouchedTicks, DateTimeKind.Utc);
            return DateTime.UtcNow - lastTouched < ActiveWindow;
        }
    }

    /// <summary>
    /// Polls until playback goes idle or <paramref name="maxWait"/> elapses,
    /// whichever comes first. Used by fire-and-forget background work (e.g.
    /// the segment analysis's FFmpeg passes) so it defers to active playback
    /// but never starves indefinitely.
    /// </summary>
    public async Task WaitForIdleAsync(TimeSpan maxWait, CancellationToken ct)
    {
        DateTime deadline = DateTime.UtcNow + maxWait;

        while (IsActive)
        {
            TimeSpan remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
                return;

            TimeSpan delay = remaining < PollInterval ? remaining : PollInterval;
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }
    }
}
