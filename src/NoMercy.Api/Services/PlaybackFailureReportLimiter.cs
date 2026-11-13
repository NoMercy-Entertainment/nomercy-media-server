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

using System.Threading.RateLimiting;

namespace NoMercy.Api.Services;

/// <summary>
/// Caps how many playback failure reports one user can send, so a looping player cannot
/// flood the owner's Activity feed.
/// </summary>
public sealed class PlaybackFailureReportLimiter : IDisposable
{
    public const int ReportsPerMinute = 10;

    private readonly PartitionedRateLimiter<Guid> _limiter = PartitionedRateLimiter.Create<
        Guid,
        Guid
    >(userId =>
        RateLimitPartition.GetSlidingWindowLimiter(
            userId,
            _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = ReportsPerMinute,
                Window = TimeSpan.FromMinutes(1),
                SegmentsPerWindow = 6,
                QueueLimit = 0,
            }
        )
    );

    public bool TryAcquire(Guid userId)
    {
        using RateLimitLease lease = _limiter.AttemptAcquire(userId);
        return lease.IsAcquired;
    }

    public void Dispose() => _limiter.Dispose();
}
