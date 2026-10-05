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

using NoMercy.Providers.Jikan;

namespace NoMercy.Tests.Providers.Jikan;

public class JikanMetadataProviderTests
{
    [Fact]
    public void RequestIntervalMs_IsConfigurable_RespectsJikanRateLimit()
    {
        JikanMetadataProvider defaultProvider = new();
        JikanMetadataProvider customProvider = new(4000);

        // Jikan's documented cap is 3 req/s / 60 req/min — 334ms is the floor
        // that keeps a single-threaded queue under 3 req/s. This must be
        // settable, not a compile-time constant.
        defaultProvider.RequestIntervalMsForTesting.Should().BeGreaterThanOrEqualTo(334);
        customProvider.RequestIntervalMsForTesting.Should().Be(4000);
    }
}
