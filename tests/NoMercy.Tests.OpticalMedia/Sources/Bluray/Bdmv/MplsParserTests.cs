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

using NoMercy.DiscFormat.Disc.Bdmv;

namespace NoMercy.DiscFormat.Tests;

public class MplsParserTests
{
    [Fact]
    public void Resolves_play_list_marks_to_presentation_seconds_across_play_items()
    {
        byte[] mpls = SyntheticBdmv.Mpls(
            playItems:
            [
                new() { Clip = "00001", DurationSeconds = 30 },
                new() { Clip = "00002", DurationSeconds = 30 },
                new() { Clip = "00003", DurationSeconds = 30 },
            ],
            markSeconds: [0, 30, 45, 75]
        );

        MplsPlaylist playlist = MplsParser.Parse(mpls);

        Assert.Equal(3, playlist.PlayItems.Count);
        Assert.Equal(90, playlist.TotalSeconds);
        Assert.Equal([0, 30, 45, 75], playlist.Marks.Select(mark => mark.Seconds));
        // Mark at 45s falls inside the second PlayItem (which starts at 30s).
        Assert.Equal(1, playlist.Marks[2].PlayItemRef);
    }

    [Fact]
    public void Decodes_the_stn_table_audio_and_subtitle_languages()
    {
        // Coding 0x83 = TrueHD, 0x81 = AC-3, 0x90 = PG subtitle (Blu-ray spec table).
        byte[] mpls = SyntheticBdmv.Mpls(
            playItems: [new() { Clip = "00000", DurationSeconds = 60 }],
            markSeconds: [0],
            audio: [(0x83, "eng"), (0x81, "ita")],
            subtitles: [(0x90, "dan"), (0x90, "eng"), (0x90, "nld")]
        );

        MplsPlaylist playlist = MplsParser.Parse(mpls);

        Assert.Equal(["eng", "ita"], playlist.AudioStreams.Select(stream => stream.Language));
        Assert.Equal(0x83, playlist.AudioStreams[0].CodingType);
        Assert.Equal(
            ["dan", "eng", "nld"],
            playlist.SubtitleStreams.Select(stream => stream.Language)
        );
        Assert.Empty(playlist.InteractiveGraphicsStreams);
    }

    [Fact]
    public void Decodes_the_stn_table_interactive_graphics_streams()
    {
        // Coding 0x91 = IG (interactive graphics) — the HDMV menu stream.
        byte[] mpls = SyntheticBdmv.Mpls(
            playItems: [new() { Clip = "00000", DurationSeconds = 60 }],
            markSeconds: [0],
            audio: [(0x81, "eng")],
            subtitles: [(0x90, "eng")],
            interactive: [(0x91, "eng"), (0x91, "ita")]
        );

        MplsPlaylist playlist = MplsParser.Parse(mpls);

        Assert.Equal(["eng"], playlist.SubtitleStreams.Select(stream => stream.Language));
        Assert.Equal(
            ["eng", "ita"],
            playlist.InteractiveGraphicsStreams.Select(stream => stream.Language)
        );
        Assert.Equal(0x91, playlist.InteractiveGraphicsStreams[0].CodingType);
    }

    [Fact]
    public void Resolves_codec_names_through_the_shared_whitelist()
    {
        byte[] mpls = SyntheticBdmv.Mpls(
            playItems: [new() { Clip = "00000", DurationSeconds = 60 }],
            markSeconds: [0],
            audio: [(0x82, "eng"), (0x85, "ita")],
            subtitles: [(0x90, "dan")],
            interactive: [(0x91, "eng")]
        );

        MplsPlaylist playlist = MplsParser.Parse(mpls);

        Assert.Equal(["dts", "dts-hd"], playlist.AudioStreams.Select(stream => stream.Codec));
        Assert.Equal("pg", playlist.SubtitleStreams[0].Codec);
        Assert.Equal("ig", playlist.InteractiveGraphicsStreams[0].Codec);
    }

    [Fact]
    public void Reads_the_textst_language_after_the_character_code_byte()
    {
        byte[] mpls = SyntheticBdmv.Mpls(
            playItems: [new() { Clip = "00000", DurationSeconds = 60 }],
            markSeconds: [0],
            subtitles: [(0x90, "deu"), (0x92, "fra")]
        );

        MplsPlaylist playlist = MplsParser.Parse(mpls);

        Assert.Equal("deu", playlist.SubtitleStreams[0].Language);
        Assert.Equal("textst", playlist.SubtitleStreams[1].Codec);
        Assert.Equal("fra", playlist.SubtitleStreams[1].Language);
    }

    [Fact]
    public void Decodes_a_multi_angle_play_item_with_its_extra_angle_clips()
    {
        byte[] mpls = SyntheticBdmv.MplsMultiAngle(
            primaryClip: "00100",
            extraAngleClips: ["00101", "00102"],
            durationSeconds: 120
        );

        MplsPlaylist playlist = MplsParser.Parse(mpls);

        Assert.True(playlist.HasMultiAngle);
        MplsPlayItem item = Assert.Single(playlist.PlayItems);
        Assert.Equal(3, item.AngleCount);
        Assert.Equal(["00101", "00102"], item.AngleClips);
    }
}
