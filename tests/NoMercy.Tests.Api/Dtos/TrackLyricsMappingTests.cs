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
using NoMercy.Api.DTOs.Music;
using NoMercy.Database.Models.Music;
using Xunit;

namespace NoMercy.Tests.Api.Dtos;

/// <summary>
/// Every track DTO embeds lyrics through one mapping, so the offset is applied
/// once in every payload and the provenance travels with the text (#72).
/// </summary>
[Trait("Category", "Unit")]
public class TrackLyricsMappingTests
{
    private static Lyric Line(double total) =>
        new()
        {
            Text = "line",
            Time = new() { Total = total },
        };

    [Fact]
    public void ApplyLyrics_WithAnOffset_ShiftsTheTimesAndReportsTheOffset()
    {
        Track track = new()
        {
            Lyrics = [Line(10.0)],
            LyricsOffset = 500,
            LyricsSource = LyricsSources.Lrclib,
            LyricsTiming = LyricsTimings.Provider,
        };
        PlaylistTrackDto dto = new();

        dto.ApplyLyrics(track);

        dto.Lyrics.Should().ContainSingle().Which.Time.Total.Should().BeApproximately(10.5, 0.0001);
        dto.LyricsOffset.Should().Be(500);
        dto.LyricsSource.Should().Be(LyricsSources.Lrclib);
        dto.LyricsTiming.Should().Be(LyricsTimings.Provider);
    }

    [Fact]
    public void ApplyLyrics_WithoutAnOffset_LeavesTheStoredTimes()
    {
        Track track = new() { Lyrics = [Line(10.0)], LyricsOffset = null };
        PlaylistTrackDto dto = new();

        dto.ApplyLyrics(track);

        dto.Lyrics.Should().ContainSingle().Which.Time.Total.Should().Be(10.0);
        dto.LyricsOffset.Should().BeNull();
        dto.LyricsSource.Should().BeNull("a row from before provenance was recorded has none");
        dto.LyricsTiming.Should().BeNull();
    }

    [Fact]
    public void ApplyLyrics_KeepsTheNegativeMarkerAndANeverFetchedTrackApart()
    {
        PlaylistTrackDto checkedNoneFound = new();
        PlaylistTrackDto neverFetched = new();

        checkedNoneFound.ApplyLyrics(new Track { Lyrics = [], LyricsOffset = 500 });
        neverFetched.ApplyLyrics(new Track { LyricsOffset = 500 });

        checkedNoneFound.Lyrics.Should().BeEmpty();
        neverFetched.Lyrics.Should().BeNull();
    }

    [Fact]
    public void ApplyLyrics_AgreesWithTheLyricsEndpointOnTheTimeline()
    {
        Track track = new() { Lyrics = [Line(10.0), Line(20.25)], LyricsOffset = 500 };
        PlaylistTrackDto dto = new();

        dto.ApplyLyrics(track);

        Lyric[] endpoint = NoMercy.Data.Services.Music.LyricTimeline.ShiftBy(
            track.Lyrics!,
            track.LyricsOffset
        );
        dto.Lyrics!.Select(line => line.Time.Total)
            .Should()
            .Equal(endpoint.Select(line => line.Time.Total));
    }
}
