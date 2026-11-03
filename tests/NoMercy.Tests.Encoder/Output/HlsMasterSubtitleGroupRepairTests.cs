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

using NoMercy.Encoder.Output;

namespace NoMercy.Tests.Encoder.Output;

/// <summary>
/// #163: a master from an older build names SUBTITLES="subs" on its variant
/// without any TYPE=SUBTITLES entry, and players invent an unlabelled track.
/// </summary>
public class HlsMasterSubtitleGroupRepairTests
{
    private const string AudioEntry =
        "#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"audio_aac\",LANGUAGE=\"jpn\",URI=\"audio_jpn_aac/audio_jpn_aac.m3u8\",NAME=\"Japanese\"";

    private const string SubtitleEntry =
        "#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID=\"subs\",NAME=\"English\",LANGUAGE=\"eng\",URI=\"subtitles/eng/full.ass\"";

    [Fact]
    public void DanglingReference_IsDropped_AndTheVariantClosesCaptions()
    {
        string master = Master(
            AudioEntry,
            "#EXT-X-STREAM-INF:BANDWIDTH=5000000,VIDEO-RANGE=SDR,AUDIO=\"audio_aac\",SUBTITLES=\"subs\""
        );

        string repaired = HlsMasterSubtitleGroupRepair.DropDanglingSubtitleGroups(master);

        repaired
            .Should()
            .Be(
                Master(
                    AudioEntry,
                    "#EXT-X-STREAM-INF:BANDWIDTH=5000000,VIDEO-RANGE=SDR,AUDIO=\"audio_aac\",CLOSED-CAPTIONS=NONE"
                )
            );
    }

    [Fact]
    public void DefinedGroup_IsLeftUntouched_AndTheSameInstanceIsReturned()
    {
        string master = Master(
            SubtitleEntry,
            "#EXT-X-STREAM-INF:BANDWIDTH=5000000,VIDEO-RANGE=SDR,SUBTITLES=\"subs\""
        );

        string repaired = HlsMasterSubtitleGroupRepair.DropDanglingSubtitleGroups(master);

        repaired.Should().BeSameAs(master, "a master without the defect is not rewritten");
    }

    [Fact]
    public void AudioGroupOfTheSameName_DoesNotCountAsASubtitleGroup()
    {
        string master = Master(
            "#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"subs\",LANGUAGE=\"eng\",URI=\"a.m3u8\",NAME=\"English\"",
            "#EXT-X-STREAM-INF:BANDWIDTH=5000000,SUBTITLES=\"subs\",CLOSED-CAPTIONS=NONE"
        );

        string repaired = HlsMasterSubtitleGroupRepair.DropDanglingSubtitleGroups(master);

        repaired.Should().NotContain("SUBTITLES=\"subs\"");
        repaired
            .Should()
            .Contain(
                "#EXT-X-STREAM-INF:BANDWIDTH=5000000,CLOSED-CAPTIONS=NONE\n",
                "an existing CLOSED-CAPTIONS attribute is kept, not doubled"
            );
    }

    [Fact]
    public void ReferenceAsFirstAttribute_KeepsTheTagWellFormed()
    {
        string master = Master("#EXT-X-STREAM-INF:SUBTITLES=\"subs\",BANDWIDTH=5000000");

        string repaired = HlsMasterSubtitleGroupRepair.DropDanglingSubtitleGroups(master);

        repaired.Should().Contain("#EXT-X-STREAM-INF:BANDWIDTH=5000000,CLOSED-CAPTIONS=NONE\n");
    }

    [Fact]
    public void CrlfLineEndings_ArePreserved()
    {
        string master =
            "#EXTM3U\r\n"
            + "#EXT-X-STREAM-INF:BANDWIDTH=5000000,SUBTITLES=\"subs\"\r\n"
            + "video_1920x1080_SDR/video_1920x1080_SDR.m3u8\r\n";

        string repaired = HlsMasterSubtitleGroupRepair.DropDanglingSubtitleGroups(master);

        repaired
            .Should()
            .Be(
                "#EXTM3U\r\n"
                    + "#EXT-X-STREAM-INF:BANDWIDTH=5000000,CLOSED-CAPTIONS=NONE\r\n"
                    + "video_1920x1080_SDR/video_1920x1080_SDR.m3u8\r\n"
            );
    }

    private static string Master(params string[] tagLines) =>
        "#EXTM3U\n#EXT-X-VERSION:6\n\n"
        + string.Join('\n', tagLines)
        + "\nvideo_1920x1080_SDR/video_1920x1080_SDR.m3u8\n";
}
