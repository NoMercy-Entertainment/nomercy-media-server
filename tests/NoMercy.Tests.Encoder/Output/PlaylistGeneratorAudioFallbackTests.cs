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

using NoMercy.Encoder.BuildingBlocks;
using NoMercy.Encoder.Codecs;
using NoMercy.Encoder.Output;
using NoMercy.Encoder.Pipeline;

namespace NoMercy.Tests.Encoder.Output;

/// <summary>
/// A master with a Dolby rendition and an AAC rendition must list one audio
/// group per codec and one variant per video and group, so a client that
/// cannot decode Dolby picks the AAC variants by CODECS.
/// </summary>
public class PlaylistGeneratorAudioFallbackTests
{
    private static VideoOutputPlan Video(int width, int height) =>
        new(
            width,
            height,
            "libx264",
            23,
            8000,
            "medium",
            "high",
            "4.0",
            false,
            "yuv420p",
            "[v0]",
            new()
        );

    private static string Generate(OutputPlan plan)
    {
        Dictionary<string, VariantMetrics> video = plan.VideoOutputs.ToDictionary(
            PlaylistGenerator.VideoVariantKey,
            _ => new VariantMetrics(5_000_000, 4_500_000)
        );
        Dictionary<string, VariantMetrics> audio = plan.AudioOutputs.ToDictionary(
            PlaylistGenerator.AudioVariantKey,
            _ => new VariantMetrics(192_000, 180_000)
        );

        return new PlaylistGenerator().GenerateMasterPlaylist(plan, "Test.Title", video, audio);
    }

    private static OutputPlan DolbyPlusAacPlan() =>
        new(
            Format: OutputFormat.Hls,
            VideoOutputs: [Video(1920, 1080), Video(1280, 720)],
            AudioOutputs:
            [
                new(
                    "copy",
                    192,
                    2,
                    48000,
                    StreamAction.Copy,
                    "eng",
                    "0:a:0",
                    SourceCodecName: "eac3"
                ),
                new("aac", 192, 2, 48000, StreamAction.Transcode, "eng", "0:a:0"),
            ],
            SubtitleOutputs: [],
            Thumbnails: null
        );

    [Fact]
    public void DolbyAndAac_EmitsOneAudioGroupPerCodec()
    {
        string master = Generate(DolbyPlusAacPlan());

        master.Should().Contain("#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"audio_eac3\"");
        master.Should().Contain("#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"audio_aac\"");
    }

    [Fact]
    public void DolbyAndAac_EmitsAVariantPerVideoAndGroupWithMatchingCodecs()
    {
        string[] infLines = Generate(DolbyPlusAacPlan())
            .Split('\n')
            .Where(l => l.StartsWith("#EXT-X-STREAM-INF"))
            .ToArray();

        infLines.Should().HaveCount(4);
        infLines
            .Count(l => l.Contains("AUDIO=\"audio_eac3\"") && l.Contains("ec-3"))
            .Should()
            .Be(2);
        infLines
            .Count(l => l.Contains("AUDIO=\"audio_aac\"") && l.Contains("mp4a.40.2"))
            .Should()
            .Be(2);
    }

    [Fact]
    public void DolbyAndAac_ListsTheOriginalGroupFirst()
    {
        string firstInf = Generate(DolbyPlusAacPlan())
            .Split('\n')
            .First(l => l.StartsWith("#EXT-X-STREAM-INF"));

        firstInf.Should().Contain("RESOLUTION=1920x1080").And.Contain("AUDIO=\"audio_eac3\"");
    }
}
