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
using System.Text;
using FluentAssertions;
using NoMercy.Encoder.Distribution;
using Xunit;

namespace NoMercy.Tests.Encoder.Distribution;

[Trait("Category", "Unit")]
public class SourcePathSignatureTests
{
    private static readonly byte[] Key = Encoding.UTF8.GetBytes("a-shared-signing-key");

    [Fact]
    public void Matches_TheSignatureComputedForTheSamePathAndTime()
    {
        string signature = SourcePathSignature.Compute(Key, "/media/film.mkv", 1_700_000_000);

        SourcePathSignature
            .Matches(Key, "/media/film.mkv", 1_700_000_000, signature)
            .Should()
            .BeTrue();
        SourcePathSignature
            .Matches(Key, "/media/other.mkv", 1_700_000_000, signature)
            .Should()
            .BeFalse();
        SourcePathSignature
            .Matches(Key, "/media/film.mkv", 1_700_000_001, signature)
            .Should()
            .BeFalse();
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(299, true)]
    [InlineData(-299, true)]
    [InlineData(301, false)]
    [InlineData(-301, false)]
    public void IsFresh_AcceptsFiveMinutesEitherWay(int secondsOff, bool expected)
    {
        DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

        SourcePathSignature.IsFresh(1_700_000_000 + secondsOff, now).Should().Be(expected);
    }
}
