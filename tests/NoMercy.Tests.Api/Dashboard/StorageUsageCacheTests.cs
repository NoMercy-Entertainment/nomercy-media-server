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

using NoMercy.Api.Services;
using Xunit;

namespace NoMercy.Tests.Api.Dashboard;

// 2026-11-06: every dashboard refresh started a new recursive walk of every
// library folder; nine ran at once over SMB, held ~200k folder handles, used up
// the SMB credits and slowed an encode's publish to one file a minute.
public class StorageUsageCacheTests
{
    [Fact]
    public async Task Concurrent_requests_share_one_walk()
    {
        SingleFlightCache<int> cache = new(TimeSpan.FromMinutes(10));
        TaskCompletionSource<int> gate = new();
        int walks = 0;

        Task<int> first = cache.GetAsync(() =>
        {
            walks++;
            return gate.Task;
        });
        Task<int> second = cache.GetAsync(() =>
        {
            walks++;
            return gate.Task;
        });
        gate.SetResult(42);

        Assert.Equal(42, await first);
        Assert.Equal(42, await second);
        Assert.Equal(1, walks);
    }

    [Fact]
    public async Task A_fresh_result_is_reused_without_a_new_walk()
    {
        DateTime now = new(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);
        SingleFlightCache<int> cache = new(TimeSpan.FromMinutes(10), () => now);
        int walks = 0;

        await cache.GetAsync(() => Task.FromResult(++walks));
        now = now.AddMinutes(9);
        int second = await cache.GetAsync(() => Task.FromResult(++walks));

        Assert.Equal(1, second);
        Assert.Equal(1, walks);
    }

    [Fact]
    public async Task An_expired_result_starts_one_new_walk()
    {
        DateTime now = new(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);
        SingleFlightCache<int> cache = new(TimeSpan.FromMinutes(10), () => now);
        int walks = 0;

        await cache.GetAsync(() => Task.FromResult(++walks));
        now = now.AddMinutes(11);
        int second = await cache.GetAsync(() => Task.FromResult(++walks));

        Assert.Equal(2, second);
    }

    [Fact]
    public async Task A_failed_walk_is_not_cached()
    {
        SingleFlightCache<int> cache = new(TimeSpan.FromMinutes(10));

        await Assert.ThrowsAsync<IOException>(() =>
            cache.GetAsync(() => Task.FromException<int>(new IOException("share gone")))
        );
        int next = await cache.GetAsync(() => Task.FromResult(7));

        Assert.Equal(7, next);
    }
}
