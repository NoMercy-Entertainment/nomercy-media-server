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

using NoMercy.MediaProcessing.Segments;

namespace NoMercy.Tests.MediaProcessing.Segments;

/// <summary>Which renditions and which language the analysis reads.</summary>
[Trait("Category", "Unit")]
public class SegmentSourceTests
{
    private const string Master = """
        #EXTM3U
        #EXT-X-VERSION:6
        #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="audio",LANGUAGE="eng",NAME="English",DEFAULT=YES,AUTOSELECT=YES,URI="audio_eng_aac/audio_eng_aac.m3u8"
        #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="audio",LANGUAGE="jpn",NAME="Japanese",DEFAULT=NO,AUTOSELECT=YES,URI="audio_jpn_aac/audio_jpn_aac.m3u8"
        #EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID="subs",LANGUAGE="eng",URI="subtitles/eng.m3u8"
        #EXT-X-STREAM-INF:BANDWIDTH=6000000,RESOLUTION=1920x1429,AUDIO="audio"
        video_1920x1429_SDR/video_1920x1429_SDR.m3u8
        #EXT-X-STREAM-INF:BANDWIDTH=2000000,RESOLUTION=1280x952,AUDIO="audio"
        video_1280x952_SDR/video_1280x952_SDR.m3u8
        """;

    [Fact]
    public void MasterPlaylist_ListsVideoVariantsAndAudioRenditions()
    {
        HlsMasterPlaylist master = HlsMasterPlaylist.Parse(Master);

        master.VideoUris.Should().Equal(
            "video_1920x1429_SDR/video_1920x1429_SDR.m3u8",
            "video_1280x952_SDR/video_1280x952_SDR.m3u8"
        );
        master.Audio.Should().Equal(
            new HlsAudioRendition("eng", "audio_eng_aac/audio_eng_aac.m3u8", true),
            new HlsAudioRendition("jpn", "audio_jpn_aac/audio_jpn_aac.m3u8", false)
        );
    }

    [Theory]
    [InlineData("ja", "jpn", true)]
    [InlineData("JPN", "jpn", true)]
    [InlineData("zh", "chi", true)]
    [InlineData("zho", "chi", true)]
    [InlineData("en", "jpn", false)]
    [InlineData(null, "jpn", false)]
    public void LanguageCodes_Of639_1And639_2_Compare(string? a, string b, bool same)
    {
        AudioLanguagePicker.Same(a, b).Should().Be(same);
    }

    [Fact]
    public void Season_UsesTheOriginalLanguage_WhenMostEpisodesHaveIt()
    {
        // Anime: the Japanese track, even when English is the default.
        List<IReadOnlyList<string>> episodes = [["eng", "jpn"], ["eng", "jpn"], ["eng"]];

        AudioLanguagePicker.Pick("ja", episodes).Should().Be("jpn");
    }

    [Fact]
    public void Season_UsesTheMostCommonLanguage_OtherwiseOneForAll()
    {
        List<IReadOnlyList<string>> episodes = [["eng"], ["eng", "ger"], ["ger"], ["eng"]];

        AudioLanguagePicker.Pick("ja", episodes).Should().Be("eng");
        AudioLanguagePicker.Pick(null, []).Should().BeNull();
    }

    [Fact]
    public void AnalysedIds_SurviveTheConfigurationValue()
    {
        SegmentAnalysisState.Parse("3, 12,x,40").Should().BeEquivalentTo([3, 12, 40]);
        SegmentAnalysisState.Parse(null).Should().BeEmpty();
    }
}
