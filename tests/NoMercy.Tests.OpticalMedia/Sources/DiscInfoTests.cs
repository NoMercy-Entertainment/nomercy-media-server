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

using NoMercy.NmSystem.Dto;
using NoMercy.OpticalMedia.Sources;

namespace NoMercy.Tests.OpticalMedia.Sources;

[Trait("Category", "Unit")]
public class DiscInfoTests
{
    private static DiscTitle MakeTitle(int index, TimeSpan duration, bool isMainFeature = false) =>
        new(
            Index: index,
            Name: $"Title {index}",
            Duration: duration,
            VideoStreams: [],
            AudioStreams: [],
            Subtitles: [],
            Chapters: [],
            EstimatedSizeBytes: 0,
            IsMainFeature: isMainFeature
        );

    [Fact]
    public void MainTitleDurationSec_SingleTitle_ReturnsDurationInSeconds()
    {
        DiscTitle title = MakeTitle(0, TimeSpan.FromSeconds(3661));
        DiscInfo disc = new(OpticalDiscType.Dvd, "TEST", [title], null, TimeSpan.Zero);

        int result = disc.MainTitleDurationSec;

        result.Should().Be(3661);
    }

    [Fact]
    public void MainTitleDurationSec_MultipleUnflaggedTitles_ReturnsDurationOfLongest()
    {
        DiscTitle title1 = MakeTitle(0, TimeSpan.FromSeconds(1800));
        DiscTitle title2 = MakeTitle(1, TimeSpan.FromSeconds(5400));
        DiscTitle title3 = MakeTitle(2, TimeSpan.FromSeconds(3600));

        DiscInfo disc = new(
            OpticalDiscType.Dvd,
            "TEST",
            [title1, title2, title3],
            null,
            TimeSpan.Zero
        );

        int result = disc.MainTitleDurationSec;

        result.Should().Be(5400);
    }

    [Fact]
    public void MainTitleDurationSec_FlaggedMainFeature_PrefersFlaggedOverLongest()
    {
        DiscTitle title1 = MakeTitle(0, TimeSpan.FromSeconds(3600), isMainFeature: true);
        DiscTitle title2 = MakeTitle(1, TimeSpan.FromSeconds(5400), isMainFeature: false);

        DiscInfo disc = new(OpticalDiscType.Dvd, "TEST", [title1, title2], null, TimeSpan.Zero);

        int result = disc.MainTitleDurationSec;

        result.Should().Be(3600);
    }

    [Fact]
    public void MainTitleDurationSec_NoTitles_ReturnsZero()
    {
        DiscInfo disc = new(OpticalDiscType.Dvd, "TEST", [], null, TimeSpan.Zero);

        int result = disc.MainTitleDurationSec;

        result.Should().Be(0);
    }

    [Fact]
    public void MainTitleDurationSec_TitleWithZeroDuration_ReturnsZero()
    {
        DiscTitle title = MakeTitle(0, TimeSpan.Zero);
        DiscInfo disc = new(OpticalDiscType.Dvd, "TEST", [title], null, TimeSpan.Zero);

        int result = disc.MainTitleDurationSec;

        result.Should().Be(0);
    }

    [Fact]
    public void MainTitleDurationSec_RoundsTruncatesDecimalSeconds()
    {
        DiscTitle title = MakeTitle(0, TimeSpan.FromSeconds(100.9));
        DiscInfo disc = new(OpticalDiscType.Dvd, "TEST", [title], null, TimeSpan.Zero);

        int result = disc.MainTitleDurationSec;

        result.Should().Be(100);
    }

    [Fact]
    public void MainTitleDurationSec_LargeDurations_Handled()
    {
        DiscTitle title = MakeTitle(0, TimeSpan.FromHours(10));
        DiscInfo disc = new(OpticalDiscType.Dvd, "TEST", [title], null, TimeSpan.Zero);

        int result = disc.MainTitleDurationSec;

        result.Should().Be(36000);
    }

    [Fact]
    public void MainTitleDurationSec_PartialSeconds_Converted()
    {
        DiscTitle title = MakeTitle(0, TimeSpan.FromSeconds(3.5));
        DiscInfo disc = new(OpticalDiscType.Dvd, "TEST", [title], null, TimeSpan.Zero);

        int result = disc.MainTitleDurationSec;

        result.Should().Be(3);
    }

    [Fact]
    public void MainTitleDurationSec_MultipleTitles_OnlyMainFeatureFlaggedButShorter()
    {
        DiscTitle mainFeature = MakeTitle(0, TimeSpan.FromSeconds(1800), isMainFeature: true);
        DiscTitle bonus1 = MakeTitle(1, TimeSpan.FromSeconds(3600), isMainFeature: false);
        DiscTitle bonus2 = MakeTitle(2, TimeSpan.FromSeconds(2700), isMainFeature: false);

        DiscInfo disc = new(
            OpticalDiscType.Dvd,
            "TEST",
            [mainFeature, bonus1, bonus2],
            null,
            TimeSpan.Zero
        );

        int result = disc.MainTitleDurationSec;

        result.Should().Be(1800);
    }

    [Fact]
    public void MainTitleDurationSec_FirstTitleIsMain_ReturnsItsDuration()
    {
        DiscTitle main = MakeTitle(0, TimeSpan.FromSeconds(7200), isMainFeature: true);
        DiscInfo disc = new(OpticalDiscType.Dvd, "TEST", [main], null, TimeSpan.Zero);

        int result = disc.MainTitleDurationSec;

        result.Should().Be(7200);
    }

    [Fact]
    public void Type_StoresDiscType()
    {
        DiscInfo disc = new(OpticalDiscType.BluRay, "TEST", [], null, TimeSpan.Zero);

        disc.Type.Should().Be(OpticalDiscType.BluRay);
    }

    [Fact]
    public void DiscLabel_StoresLabel()
    {
        DiscInfo disc = new(OpticalDiscType.Dvd, "MY_DISC", [], null, TimeSpan.Zero);

        disc.DiscLabel.Should().Be("MY_DISC");
    }

    [Fact]
    public void Titles_StoresTitleArray()
    {
        DiscTitle title = MakeTitle(0, TimeSpan.FromSeconds(3600));
        DiscInfo disc = new(OpticalDiscType.Dvd, "TEST", [title], null, TimeSpan.Zero);

        disc.Titles.Should().HaveCount(1);
        disc.Titles[0].Should().Be(title);
    }

    [Fact]
    public void TotalDuration_StoresTotalDuration()
    {
        DiscInfo disc = new(OpticalDiscType.Dvd, "TEST", [], null, TimeSpan.FromHours(2));

        disc.TotalDuration.Should().Be(TimeSpan.FromHours(2));
    }

    [Fact]
    public void DiscTitle_Stores_EmbeddedTitle()
    {
        DiscInfo disc = new(
            OpticalDiscType.BluRay,
            "VOLUME_LABEL",
            [],
            null,
            TimeSpan.Zero,
            DiscTitle: "The Dark Knight"
        );

        disc.DiscTitle.Should().Be("The Dark Knight");
    }

    [Fact]
    public void Protection_StoresProtectionInfo()
    {
        DiscProtection protection = new("AACS", "ABC123", "AACS protected");
        DiscInfo disc = new(OpticalDiscType.BluRay, "TEST", [], null, TimeSpan.Zero, protection);

        disc.Protection.Should().Be(protection);
    }

    [Fact]
    public void Protection_Null_WhenNotProvided()
    {
        DiscInfo disc = new(OpticalDiscType.Dvd, "TEST", [], null, TimeSpan.Zero);

        disc.Protection.Should().BeNull();
    }

    [Fact]
    public void AudioTracks_StoresAudioTracks()
    {
        DiscTrack track = new(0, "Track 1", "Artist", TimeSpan.FromSeconds(180), 44100, 2);
        DiscInfo disc = new(OpticalDiscType.Cd, "AUDIO_CD", [], [track], TimeSpan.Zero);

        disc.AudioTracks.Should().HaveCount(1);
        disc.AudioTracks![0].Should().Be(track);
    }

    [Fact]
    public void AudioTracks_Null_ForVideoDiscs()
    {
        DiscInfo disc = new(OpticalDiscType.Dvd, "TEST", [], null, TimeSpan.Zero);

        disc.AudioTracks.Should().BeNull();
    }
}
