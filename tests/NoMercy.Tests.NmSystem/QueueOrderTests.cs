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
using FluentAssertions;
using NoMercy.NmSystem.Extensions;
using Xunit;

namespace NoMercy.Tests.NmSystem;

[Trait("Category", "Unit")]
public class QueueOrderTests
{
    [Theory]
    [InlineData(20, new[] { 21, 22 })]
    [InlineData(21, new[] { 22, 20 })]
    [InlineData(22, new[] { 20, 21 })]
    public void QueueAfter_PlaysEverythingAfterTheCurrentItemThenWrapsAround(
        int current,
        int[] expected
    )
    {
        List<int> items = [20, 21, 22];

        items.QueueAfter(item => item == current).Should().Equal(expected);
    }

    [Fact]
    public void QueueAfter_NoCurrentItem_KeepsTheWholeListInOrder()
    {
        List<int> items = [1, 2];

        items.QueueAfter(item => item == 999).Should().Equal(1, 2);
    }
}
