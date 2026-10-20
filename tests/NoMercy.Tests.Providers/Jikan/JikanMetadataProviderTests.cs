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

using NoMercy.Providers.Helpers;
using NoMercy.Providers.Jikan;

namespace NoMercy.Tests.Providers.Jikan;

public class JikanMetadataProviderTests
{
    [Fact]
    public void RequestIntervalMs_IsConfigurable_RespectsJikanRateLimit()
    {
        JikanMetadataProvider defaultProvider = new();
        JikanMetadataProvider customProvider = new(4000);

        // Jikan's documented cap is 3 req/s and 60 req/min. A bulk import runs
        // for minutes, so the per-minute cap is the binding one and 1000ms is the
        // floor for a single-threaded queue. This must be settable, not a
        // compile-time constant.
        defaultProvider.RequestIntervalMsForTesting.Should().BeGreaterThanOrEqualTo(1000);
        customProvider.RequestIntervalMsForTesting.Should().Be(4000);
    }

    // The static JikanClient entry points pace through the Jikan family's own
    // queue default, so a process that never constructs the provider first must
    // get the same spacing as one that does.
    [Fact]
    public void DefaultRequestInterval_MatchesTheJikanQueueDefault()
    {
        JikanMetadataProvider defaultProvider = new();

        ProviderQueues
            .OptionsFor(HttpClientNames.Jikan)
            .Interval.Should()
            .Be(defaultProvider.RequestIntervalMsForTesting);
    }
}
