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

using NoMercy.Providers.Lrclib.Client;
using NoMercy.Providers.Lrclib.Models;
using NoMercy.Providers.NoMercy.Models;

namespace NoMercy.Tests.Providers.Lrclib.Client;

[Trait("Category", "Unit")]
public class LrclibClientToCandidateTests
{
    [Fact]
    public void ToCandidate_WithInstrumental_ReturnsNull()
    {
        LrclibSongResult result = new()
        {
            TrackName = "Symphony No. 9",
            ArtistName = "Beethoven",
            Duration = 300,
            Instrumental = true,
            SyncedLyrics = "[00:00.00]Instrumental",
        };

        LyricCandidate? candidate = LrclibClient.ToCandidate(result);

        candidate.Should().BeNull();
    }

    [Fact]
    public void ToCandidate_WithoutSyncedOrPlainLyrics_ReturnsNull()
    {
        LrclibSongResult result = new()
        {
            TrackName = "Song Name",
            ArtistName = "Artist Name",
            Duration = 300,
            Instrumental = false,
            SyncedLyrics = "",
            PlainLyrics = "",
        };

        LyricCandidate? candidate = LrclibClient.ToCandidate(result);

        candidate.Should().BeNull();
    }

    [Fact]
    public void ToCandidate_PrefersSyncedOverPlain()
    {
        LrclibSongResult result = new()
        {
            TrackName = "Song Name",
            ArtistName = "Artist Name",
            Duration = 200,
            Instrumental = false,
            SyncedLyrics = "[00:01.50]Synced line",
            PlainLyrics = "Plain line",
        };

        LyricCandidate? candidate = LrclibClient.ToCandidate(result);

        candidate.Should().NotBeNull();
        candidate!.HasSyncedLyrics.Should().BeTrue();
    }

    [Fact]
    public void ToCandidate_UsesPlainWhenNoSynced()
    {
        LrclibSongResult result = new()
        {
            TrackName = "Song Name",
            ArtistName = "Artist Name",
            Duration = 200,
            Instrumental = false,
            SyncedLyrics = "",
            PlainLyrics = "Plain line",
        };

        LyricCandidate? candidate = LrclibClient.ToCandidate(result);

        candidate.Should().NotBeNull();
        candidate!.HasSyncedLyrics.Should().BeFalse();
    }

    [Fact]
    public void ToCandidate_RoundsPositiveDuration()
    {
        LrclibSongResult result = new()
        {
            TrackName = "Song Name",
            ArtistName = "Artist Name",
            Duration = 200.6,
            Instrumental = false,
            SyncedLyrics = "[00:01.00]Line",
        };

        LyricCandidate? candidate = LrclibClient.ToCandidate(result);

        candidate!.DurationSeconds.Should().Be(201);
    }

    [Fact]
    public void ToCandidate_RoundsDown()
    {
        LrclibSongResult result = new()
        {
            TrackName = "Song Name",
            ArtistName = "Artist Name",
            Duration = 200.4,
            Instrumental = false,
            SyncedLyrics = "[00:01.00]Line",
        };

        LyricCandidate? candidate = LrclibClient.ToCandidate(result);

        candidate!.DurationSeconds.Should().Be(200);
    }

    [Fact]
    public void ToCandidate_WithZeroDuration_ReturnsDurationNull()
    {
        LrclibSongResult result = new()
        {
            TrackName = "Song Name",
            ArtistName = "Artist Name",
            Duration = 0,
            Instrumental = false,
            SyncedLyrics = "[00:01.00]Line",
        };

        LyricCandidate? candidate = LrclibClient.ToCandidate(result);

        candidate!.DurationSeconds.Should().BeNull();
    }

    [Fact]
    public void ToCandidate_MapsTitleAndArtist()
    {
        LrclibSongResult result = new()
        {
            TrackName = "Bohemian Rhapsody",
            ArtistName = "Queen",
            Duration = 354,
            Instrumental = false,
            SyncedLyrics = "[00:01.00]Line",
        };

        LyricCandidate? candidate = LrclibClient.ToCandidate(result);

        candidate!.Title.Should().Be("Bohemian Rhapsody");
        candidate.Artist.Should().Be("Queen");
    }

    [Fact]
    public void ToCandidate_ParsesSyncedLyrics()
    {
        LrclibSongResult result = new()
        {
            TrackName = "Song",
            ArtistName = "Artist",
            Duration = 100,
            Instrumental = false,
            SyncedLyrics = "[00:01.50]First\n[00:03.20]Second",
        };

        LyricCandidate? candidate = LrclibClient.ToCandidate(result);

        candidate!.Lines.Should().HaveCount(2);
        candidate.Lines[0].Text.Should().Be("First");
        candidate.Lines[0].Time.Total.Should().Be(1.5);
        candidate.Lines[1].Text.Should().Be("Second");
        candidate.Lines[1].Time.Total.Should().Be(3.2);
    }

    [Fact]
    public void ToCandidate_FiltersBlanklinesFromLyrics()
    {
        LrclibSongResult result = new()
        {
            TrackName = "Song",
            ArtistName = "Artist",
            Duration = 100,
            Instrumental = false,
            SyncedLyrics = "[00:01.00]First\n\n[00:02.00]Second",
        };

        LyricCandidate? candidate = LrclibClient.ToCandidate(result);

        candidate!.Lines.Should().HaveCount(2);
        candidate.Lines[0].Text.Should().Be("First");
        candidate.Lines[1].Text.Should().Be("Second");
    }

    [Fact]
    public void ToCandidate_WithOnlyBlankLines_ReturnsNull()
    {
        LrclibSongResult result = new()
        {
            TrackName = "Song",
            ArtistName = "Artist",
            Duration = 100,
            Instrumental = false,
            SyncedLyrics = "\n\n\n",
        };

        LyricCandidate? candidate = LrclibClient.ToCandidate(result);

        candidate.Should().BeNull();
    }

    [Fact]
    public void ToCandidate_ParsesUnsyncedLines()
    {
        LrclibSongResult result = new()
        {
            TrackName = "Song",
            ArtistName = "Artist",
            Duration = 100,
            Instrumental = false,
            PlainLyrics = "First line\nSecond line",
        };

        LyricCandidate? candidate = LrclibClient.ToCandidate(result);

        candidate!.Lines.Should().HaveCount(2);
        candidate.Lines[0].Text.Should().Be("First line");
        candidate.Lines[0].Time.Total.Should().Be(0);
        candidate.Lines[1].Text.Should().Be("Second line");
        candidate.Lines[1].Time.Total.Should().Be(0);
    }

    [Fact]
    public void ToCandidate_UnrecognizedTimestamp_TreatsAsUnsynced()
    {
        LrclibSongResult result = new()
        {
            TrackName = "Song",
            ArtistName = "Artist",
            Duration = 100,
            Instrumental = false,
            SyncedLyrics = "Line without timestamp\n[00:01.00]Timed line",
        };

        LyricCandidate? candidate = LrclibClient.ToCandidate(result);

        candidate!.Lines.Should().HaveCount(2);
        candidate.Lines[0].Text.Should().Be("Line without timestamp");
        candidate.Lines[0].Time.Total.Should().Be(0);
        candidate.Lines[1].Text.Should().Be("Timed line");
        candidate.Lines[1].Time.Total.Should().Be(1.0);
    }

    [Fact]
    public void ToCandidate_ParsesTimestampCorrectly()
    {
        LrclibSongResult result = new()
        {
            TrackName = "Song",
            ArtistName = "Artist",
            Duration = 300,
            Instrumental = false,
            SyncedLyrics = "[02:15.42]Line at 2:15.42",
        };

        LyricCandidate? candidate = LrclibClient.ToCandidate(result);

        candidate!.Lines[0].Time.Minutes.Should().Be(2);
        candidate.Lines[0].Time.Seconds.Should().Be(15);
        candidate.Lines[0].Time.Hundredths.Should().Be(42);
        candidate.Lines[0].Time.Total.Should().Be(135.42);
    }

    [Fact]
    public void ToCandidate_ParsesMillisecondPrecisionTimestamp()
    {
        // Lrclib serves plenty of LRC with 3-digit millisecond fractions
        // ([mm:ss.mmm]). The parser must read those as milliseconds and strip the
        // tag from the line, not fail to match and leave the raw "[00:01.900]" in
        // the displayed text with a zero timestamp.
        LrclibSongResult result = new()
        {
            TrackName = "Song",
            ArtistName = "Artist",
            Duration = 300,
            Instrumental = false,
            SyncedLyrics = "[00:00.000](Future)\n[00:01.900](Future nostalgia)",
        };

        LyricCandidate? candidate = LrclibClient.ToCandidate(result);

        candidate!.Lines.Should().HaveCount(2);
        candidate.Lines[0].Text.Should().Be("(Future)");
        candidate.Lines[0].Time.Total.Should().Be(0);
        candidate.Lines[1].Text.Should().Be("(Future nostalgia)");
        candidate.Lines[1].Time.Total.Should().Be(1.9);
        candidate.Lines[1].Time.Seconds.Should().Be(1);
        candidate.Lines[1].Time.Hundredths.Should().Be(90);
    }

    [Fact]
    public void ToCandidate_ParsesColonFractionSeparator()
    {
        // Some LRC uses a colon rather than a dot before the fraction
        // ([mm:ss:cc]); it must parse, not be left in the text.
        LrclibSongResult result = new()
        {
            TrackName = "Song",
            ArtistName = "Artist",
            Duration = 300,
            Instrumental = false,
            SyncedLyrics = "[00:00:00]Intro\n[01:05:50]Later line",
        };

        LyricCandidate? candidate = LrclibClient.ToCandidate(result);

        candidate!.Lines[0].Text.Should().Be("Intro");
        candidate.Lines[0].Time.Total.Should().Be(0);
        candidate.Lines[1].Text.Should().Be("Later line");
        candidate.Lines[1].Time.Total.Should().Be(65.5);
    }

    [Fact]
    public void ToCandidate_ParsesTimestampWithoutFraction()
    {
        LrclibSongResult result = new()
        {
            TrackName = "Song",
            ArtistName = "Artist",
            Duration = 300,
            Instrumental = false,
            SyncedLyrics = "[01:05]No fraction",
        };

        LyricCandidate? candidate = LrclibClient.ToCandidate(result);

        candidate!.Lines[0].Text.Should().Be("No fraction");
        candidate.Lines[0].Time.Total.Should().Be(65);
    }

    [Fact]
    public void ToCandidate_StripsSurroundingWhitespace()
    {
        LrclibSongResult result = new()
        {
            TrackName = "Song",
            ArtistName = "Artist",
            Duration = 100,
            Instrumental = false,
            SyncedLyrics = "  [00:01.00]  Text with spaces  ",
        };

        LyricCandidate? candidate = LrclibClient.ToCandidate(result);

        candidate!.Lines[0].Text.Should().Be("Text with spaces");
    }

    [Theory]
    [InlineData("")]
    public void ToCandidate_WithEmptyOrNullSyncedLyrics_UsesPlainsyncedIfAvailable(string synced)
    {
        LrclibSongResult result = new()
        {
            TrackName = "Song",
            ArtistName = "Artist",
            Duration = 100,
            Instrumental = false,
            SyncedLyrics = synced ?? "",
            PlainLyrics = "Plain lyrics available",
        };

        LyricCandidate? candidate = LrclibClient.ToCandidate(result);

        candidate.Should().NotBeNull();
        candidate!.HasSyncedLyrics.Should().BeFalse();
    }

    [Fact]
    public void ToCandidate_RepeatedTimestamps_EmitOneLinePerTagWithCleanText()
    {
        LrclibSongResult result = new()
        {
            TrackName = "Song",
            ArtistName = "Artist",
            Duration = 100,
            Instrumental = false,
            SyncedLyrics = "[ar:X]\n[00:12.00][01:30.00]Chorus",
        };

        LyricCandidate? candidate = LrclibClient.ToCandidate(result);

        candidate!.Lines.Select(line => line.Text).Should().Equal("Chorus", "Chorus");
        candidate.Lines[0].Time.Total.Should().Be(12.0);
        candidate.Lines[1].Time.Total.Should().Be(90.0);
        candidate.Lines[1].Time.Minutes.Should().Be(1);
        candidate.Lines[1].Time.Seconds.Should().Be(30);
    }

    [Fact]
    public void ToCandidate_DropsIdTagsFromSyncedLyrics()
    {
        LrclibSongResult result = new()
        {
            TrackName = "Song",
            ArtistName = "Artist",
            Duration = 100,
            Instrumental = false,
            SyncedLyrics = "[ar:Artist]\n[ti:Song]\n[offset:+500]\n[00:01.00]First",
        };

        LyricCandidate? candidate = LrclibClient.ToCandidate(result);

        candidate!.Lines.Should().ContainSingle().Which.Text.Should().Be("First");
    }

    [Fact]
    public void ToCandidate_OrdersRepeatedTimestampsChronologically()
    {
        LrclibSongResult result = new()
        {
            TrackName = "Song",
            ArtistName = "Artist",
            Duration = 100,
            Instrumental = false,
            SyncedLyrics = "[00:12.00][01:30.00]Chorus\n[00:20.00]Verse",
        };

        LyricCandidate? candidate = LrclibClient.ToCandidate(result);

        candidate!.Lines.Select(line => line.Time.Total).Should().Equal(12.0, 20.0, 90.0);
        candidate.Lines.Select(line => line.Text).Should().Equal("Chorus", "Verse", "Chorus");
    }

    [Fact]
    public void ToCandidate_KeepsBracketedPlainLyricLines()
    {
        // Plain lyrics carry no LRC header, so a bracketed section label with a
        // colon is lyric text, not an ID tag to drop.
        LrclibSongResult result = new()
        {
            TrackName = "Song",
            ArtistName = "Artist",
            Duration = 100,
            Instrumental = false,
            PlainLyrics = "[Chorus: Artist]\nFirst line",
        };

        LyricCandidate? candidate = LrclibClient.ToCandidate(result);

        candidate!.Lines.Select(line => line.Text).Should().Equal("[Chorus: Artist]", "First line");
    }
}
