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

using NoMercy.Providers.AniList;

namespace NoMercy.Tests.Providers.AniList;

public class AniListMetadataProviderTests
{
    [Fact]
    public void RequestIntervalMs_IsConfigurable_NotHardcodedToAniListDefault()
    {
        AniListMetadataProvider defaultProvider = new();
        AniListMetadataProvider customProvider = new(4000);

        // AniList's published cap has already changed once (90 -> 30 req/min);
        // this must be settable, not a compile-time constant.
        defaultProvider.RequestIntervalMsForTesting.Should().BeGreaterThan(0);
        customProvider.RequestIntervalMsForTesting.Should().Be(4000);
    }
}
