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

using NoMercy.Encoder.Distribution;

namespace NoMercy.Tests.Encoder.Distribution;

/// <summary>
/// The introspect cache only checked TTL on read and never removed entries, so
/// rotated worker tokens accumulated as permanent keys. ExpiredIntrospectKeys is
/// what the write-path sweep uses to keep it bounded.
/// </summary>
public class LicenseTokenClientCacheTests
{
    private static KeyValuePair<string, (IntrospectResult Result, DateTime CachedAt)> Entry(
        string token,
        DateTime cachedAt
    ) => new(token, (new IntrospectResult(Active: true, Scopes: [], Message: null), cachedAt));

    [Fact]
    public void ExpiredIntrospectKeys_SelectsEntriesAtOrPastTtl_KeepsFreshOnes()
    {
        DateTime now = new(2026, 1, 1, 0, 0, 30, DateTimeKind.Utc);
        TimeSpan ttl = TimeSpan.FromSeconds(30);
        List<KeyValuePair<string, (IntrospectResult Result, DateTime CachedAt)>> entries =
        [
            Entry("fresh", now.AddSeconds(-5)),
            Entry("edge", now.AddSeconds(-30)),
            Entry("stale", now.AddSeconds(-120)),
        ];

        List<string> expired = LicenseTokenClient.ExpiredIntrospectKeys(entries, now, ttl).ToList();

        Assert.Equal(2, expired.Count);
        Assert.Contains("edge", expired);
        Assert.Contains("stale", expired);
        Assert.DoesNotContain("fresh", expired);
    }

    [Fact]
    public void ExpiredIntrospectKeys_AllFresh_SelectsNothing()
    {
        DateTime now = new(2026, 1, 1, 0, 0, 30, DateTimeKind.Utc);
        List<KeyValuePair<string, (IntrospectResult Result, DateTime CachedAt)>> entries =
        [
            Entry("a", now.AddSeconds(-1)),
            Entry("b", now.AddSeconds(-10)),
        ];

        Assert.Empty(
            LicenseTokenClient.ExpiredIntrospectKeys(entries, now, TimeSpan.FromSeconds(30))
        );
    }
}
