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

using NoMercy.MediaProcessing.Intake;

namespace NoMercy.Tests.MediaProcessing.Intake;

[Trait("Category", "Unit")]
public class EpisodeClaimsTests
{
    private sealed record Selected(string MediaId, string Path);

    [Theory]
    [InlineData("Show.S01E01.mkv", true)]
    [InlineData("Show 1x01.mkv", true)]
    [InlineData("Show - 175 - Title.mkv", true)]
    [InlineData("Show NCOP.mkv", false)]
    [InlineData("Show.2024.mkv", false)]
    public void DeclaresEpisode_OnlyForAnExplicitEpisodeMarker(string fileName, bool expected)
    {
        EpisodeClaims.DeclaresEpisode(fileName).Should().Be(expected);
    }

    [Fact]
    public void PickOnePerEpisode_TheFileNamingTheEpisodeBeatsTheOneSortedFirst()
    {
        Selected opening = new("ep-1", "/staging/Show NCOP.mkv");
        Selected episode = new("ep-1", "/staging/Show S01E01.mkv");
        Selected other = new("ep-2", "/staging/Show S01E02.mkv");

        (List<Selected> selected, List<string> collided) = EpisodeClaims.PickOnePerEpisode(
            [opening, episode, other],
            file => file.MediaId,
            file => file.Path
        );

        selected.Should().Equal(episode, other);
        collided.Should().Equal("Show NCOP.mkv");
    }

    [Fact]
    public void PickOnePerEpisode_UnmatchedFilesAreAllKept()
    {
        Selected first = new("", "/staging/a.mkv");
        Selected second = new("", "/staging/b.mkv");

        (List<Selected> selected, List<string> collided) = EpisodeClaims.PickOnePerEpisode(
            [first, second],
            file => file.MediaId,
            file => file.Path
        );

        selected.Should().Equal(first, second);
        collided.Should().BeEmpty();
    }
}
