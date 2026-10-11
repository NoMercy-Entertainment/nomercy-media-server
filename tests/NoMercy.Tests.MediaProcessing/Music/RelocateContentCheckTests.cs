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

using NoMercy.MediaProcessing.Recordings;

namespace NoMercy.Tests.MediaProcessing.Music;

/// <summary>
/// 2026-10-07: a repair matched files to the wrong release and track, and the organize step
/// renamed them to fit: the file holding "Heavy" became "1-08 Hocus Pocus.mp3". A file is
/// only renamed to a track its own content confirms.
/// </summary>
[Trait("Category", "Unit")]
public class RelocateContentCheckTests
{
    private static readonly TimeSpan NoDuration = TimeSpan.Zero;

    [Theory]
    [InlineData("Heavy", "Hocus Pocus")]
    [InlineData("Fire in Your Eyes", "Eastern Jam")]
    [InlineData("Rolle Met Bolle", "Mond Vol Tanden")]
    [InlineData("Poor Leno (Jakatta radio mix)", "So Easy")]
    public void A_title_tag_naming_another_song_refuses_the_move(
        string fileTitle,
        string trackTitle
    )
    {
        Assert.False(RecordingManager.ContentConfirmsTrack(fileTitle, NoDuration, trackTitle, 0));
    }

    [Theory]
    [InlineData("Umbrella", "Umbrella")]
    [InlineData("Tempus Vernum", "Tempus Vernum")]
    [InlineData("Alles of Niets", "Alles Of Niets")]
    [InlineData("Deora Ar Mo Chroí", "Deora Ar Mo Chroi")]
    [InlineData("40 Years Back / Come", "40 Years Back/Come")]
    [InlineData("Poor Leno (Jakatta radio mix)", "Poor Leno (Jakatta radio mix)")]
    public void A_title_tag_naming_the_same_song_allows_the_move(
        string fileTitle,
        string trackTitle
    )
    {
        Assert.True(RecordingManager.ContentConfirmsTrack(fileTitle, NoDuration, trackTitle, 0));
    }

    [Fact]
    public void Without_a_title_tag_the_length_decides()
    {
        TimeSpan file = TimeSpan.FromSeconds(237.384);

        Assert.True(
            RecordingManager.ContentConfirmsTrack(null, file, "Ain't That Loving You", 236.0)
        );
        Assert.False(RecordingManager.ContentConfirmsTrack("", file, "Queen Majesty", 252.0));
    }

    [Fact]
    public void Without_a_title_tag_or_a_length_nothing_confirms_the_move()
    {
        Assert.False(
            RecordingManager.ContentConfirmsTrack(null, NoDuration, "Queen Majesty", 252.0)
        );
        Assert.False(
            RecordingManager.ContentConfirmsTrack(
                null,
                TimeSpan.FromSeconds(237),
                "Queen Majesty",
                0
            )
        );
    }
}
