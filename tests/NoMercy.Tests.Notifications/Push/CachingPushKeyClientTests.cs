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

using NoMercy.Notifications.Push;
using Xunit;

namespace NoMercy.Tests.Notifications.Push;

public class CachingPushKeyClientTests
{
    private sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }

    private static readonly PushSubscriptionKey[] SampleKeys = [new(7, "BJ1V", "c2Vj")];

    [Fact]
    public async Task GetKeysAsync_Within_Ttl_Does_Not_Call_The_Inner_Client_Again()
    {
        FakePushKeyClient fake = new(() => Task.FromResult(SampleKeys));
        ManualTimeProvider clock = new(DateTimeOffset.UnixEpoch);
        CachingPushKeyClient cache = new(fake, TimeSpan.FromMinutes(15), clock);

        PushSubscriptionKey[] first = await cache.GetKeysAsync("token");
        clock.Advance(TimeSpan.FromMinutes(1));
        PushSubscriptionKey[] second = await cache.GetKeysAsync("token");

        Assert.Equal(1, fake.CallCount);
        Assert.Same(first, second);
    }

    [Fact]
    public async Task GetKeysAsync_After_Ttl_Expires_Calls_The_Inner_Client_Again()
    {
        FakePushKeyClient fake = new(() => Task.FromResult(SampleKeys));
        ManualTimeProvider clock = new(DateTimeOffset.UnixEpoch);
        CachingPushKeyClient cache = new(fake, TimeSpan.FromMinutes(15), clock);

        await cache.GetKeysAsync("token");
        clock.Advance(TimeSpan.FromMinutes(16));
        await cache.GetKeysAsync("token");

        Assert.Equal(2, fake.CallCount);
    }

    [Fact]
    public async Task GetKeysAsync_Returns_Empty_Rather_Than_Throwing_When_The_Saas_Is_Unreachable()
    {
        FakePushKeyClient fake = new(() =>
            Task.FromException<PushSubscriptionKey[]>(new HttpRequestException("offline"))
        );
        CachingPushKeyClient cache = new(fake);

        PushSubscriptionKey[] keys = await cache.GetKeysAsync("token");

        Assert.Empty(keys);
    }

    [Fact]
    public async Task GetKeysAsync_Collapses_Concurrent_Misses_Into_One_Inner_Call()
    {
        TaskCompletionSource<PushSubscriptionKey[]> gate = new();
        FakePushKeyClient fake = new(() => gate.Task);
        CachingPushKeyClient cache = new(fake);

        Task<PushSubscriptionKey[]> first = cache.GetKeysAsync("token");
        Task<PushSubscriptionKey[]> second = cache.GetKeysAsync("token");

        gate.SetResult(SampleKeys);

        PushSubscriptionKey[] firstResult = await first;
        PushSubscriptionKey[] secondResult = await second;

        Assert.Equal(1, fake.CallCount);
        Assert.Same(firstResult, secondResult);
    }

    [Fact]
    public async Task GetKeysAsync_Retries_Once_The_Failure_Ttl_Has_Passed()
    {
        int attempt = 0;
        FakePushKeyClient fake = new(() =>
        {
            attempt++;
            return attempt == 1
                ? Task.FromException<PushSubscriptionKey[]>(new HttpRequestException("offline"))
                : Task.FromResult(SampleKeys);
        });
        ManualTimeProvider clock = new(DateTimeOffset.UnixEpoch);
        CachingPushKeyClient cache = new(
            fake,
            TimeSpan.FromMinutes(15),
            clock,
            TimeSpan.FromMinutes(1)
        );

        PushSubscriptionKey[] failed = await cache.GetKeysAsync("token");
        clock.Advance(TimeSpan.FromMinutes(2));
        PushSubscriptionKey[] recovered = await cache.GetKeysAsync("token");

        Assert.Empty(failed);
        Assert.Same(SampleKeys, recovered);
    }

    /// <summary>
    /// With no negative cache an unreachable SaaS costs the full HTTP timeout
    /// on every single event, which is the storm the cache exists to prevent.
    /// </summary>
    [Fact]
    public async Task GetKeysAsync_Does_Not_Hit_The_Saas_Again_Inside_The_Failure_Ttl()
    {
        FakePushKeyClient fake = new(() =>
            Task.FromException<PushSubscriptionKey[]>(new HttpRequestException("offline"))
        );
        ManualTimeProvider clock = new(DateTimeOffset.UnixEpoch);
        CachingPushKeyClient cache = new(
            fake,
            TimeSpan.FromMinutes(15),
            clock,
            TimeSpan.FromMinutes(1)
        );

        await cache.GetKeysAsync("token");
        clock.Advance(TimeSpan.FromSeconds(30));
        PushSubscriptionKey[] second = await cache.GetKeysAsync("token");

        Assert.Equal(1, fake.CallCount);
        Assert.Empty(second);
    }

    [Fact]
    public async Task GetKeysAsync_Refetches_When_The_Access_Token_Changes()
    {
        FakePushKeyClient fake = new(() => Task.FromResult(SampleKeys));
        ManualTimeProvider clock = new(DateTimeOffset.UnixEpoch);
        CachingPushKeyClient cache = new(fake, TimeSpan.FromMinutes(15), clock);

        await cache.GetKeysAsync("token-before-reauth");
        clock.Advance(TimeSpan.FromMinutes(1));
        await cache.GetKeysAsync("token-after-reauth");

        Assert.Equal(2, fake.CallCount);
    }

    /// <summary>
    /// A refresh for a new token that is cancelled mid-fetch must not leave the
    /// new token paired with the previous token's keys and expiry.
    /// </summary>
    [Fact]
    public async Task GetKeysAsync_Fetches_Again_For_A_Token_Whose_Refresh_Was_Cancelled()
    {
        PushSubscriptionKey[] keysForA = [new(1, "A-p256dh", "A-auth")];
        PushSubscriptionKey[] keysForB = [new(2, "B-p256dh", "B-auth")];
        using CancellationTokenSource refreshB = new();
        int attempt = 0;
        FakePushKeyClient fake = new(() =>
        {
            attempt++;
            if (attempt == 1)
                return Task.FromResult(keysForA);
            if (attempt == 2)
            {
                refreshB.Cancel();
                return Task.FromCanceled<PushSubscriptionKey[]>(refreshB.Token);
            }
            return Task.FromResult(keysForB);
        });
        ManualTimeProvider clock = new(DateTimeOffset.UnixEpoch);
        CachingPushKeyClient cache = new(fake, TimeSpan.FromMinutes(15), clock);

        await cache.GetKeysAsync("token-a");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            cache.GetKeysAsync("token-b", refreshB.Token)
        );
        PushSubscriptionKey[] afterCancel = await cache.GetKeysAsync("token-b");

        Assert.Equal(3, fake.CallCount);
        Assert.Same(keysForB, afterCancel);
    }
}
