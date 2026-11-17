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

using NoMercy.Data.Services.SegmentAnalysis;
using NoMercy.Database.Models.Media;

namespace NoMercy.Tests.MediaProcessing.Segments;

[Trait("Category", "Unit")]
public class ContentSegmentTimesTests
{
    [Fact]
    public void CreditsStart_IsTheCreditsSegmentStart()
    {
        ContentSegmentTimes
            .CreditsStart([
                Row(ContentSegmentType.Intro, 5, 82),
                Row(ContentSegmentType.Credits, 1281.64, 1361.08),
            ])
            .Should()
            .Be(1281.64);
    }

    [Fact]
    public void CreditsStart_WithSeveral_IsTheLatest_AndOldNegativeOutrosAreIgnored()
    {
        ContentSegmentTimes
            .CreditsStart([
                Row(ContentSegmentType.Outro, 1270, 1300),
                Row(ContentSegmentType.Credits, 1281.64, 1361.08),
                Row(ContentSegmentType.Outro, -240, -150),
            ])
            .Should()
            .Be(1281.64);
    }

    [Fact]
    public void CreditsStart_WithoutCredits_IsNull()
    {
        ContentSegmentTimes.CreditsStart([Row(ContentSegmentType.Intro, 5, 82)]).Should().BeNull();
        ContentSegmentTimes
            .CreditsStart([Row(ContentSegmentType.Outro, -240, -150)])
            .Should()
            .BeNull();
    }

    [Fact]
    public void ContentEnd_IsReadFromTheCreditsRow_AndNullWithoutOne()
    {
        ContentSegment credits = Row(ContentSegmentType.Credits, 1281.64, 1361.08);
        credits.ContentEndSeconds = 1377.27;

        ContentSegmentTimes
            .ContentEnd([Row(ContentSegmentType.Intro, 5, 82), credits])
            .Should()
            .Be(1377.27);
        ContentSegmentTimes
            .ContentEnd([Row(ContentSegmentType.Credits, 1281.64, 1361.08)])
            .Should()
            .BeNull();
    }

    private static ContentSegment Row(ContentSegmentType type, double start, double end) =>
        new()
        {
            SegmentType = type,
            StartSeconds = start,
            EndSeconds = end,
            Source = "analysis",
        };
}
