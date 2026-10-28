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

using NoMercy.MediaProcessing.Music;

namespace NoMercy.Tests.MediaProcessing.Music;

[Trait("Category", "Unit")]
public sealed class MissingFileGuardTests
{
    [Theory]
    [InlineData(1000, 100, false)]
    [InlineData(1000, 101, true)]
    [InlineData(10000, 501, true)]
    [InlineData(10000, 500, false)]
    [InlineData(3, 1, false)]
    [InlineData(20, 3, false)]
    [InlineData(20, 5, true)]
    [InlineData(20, 2, false)]
    public void Skips_deletes_past_ten_percent_or_five_hundred_files(
        int known,
        int missing,
        bool skip
    ) => MissingFileGuard.ShouldSkipDeletes(known, missing).Should().Be(skip);
}
