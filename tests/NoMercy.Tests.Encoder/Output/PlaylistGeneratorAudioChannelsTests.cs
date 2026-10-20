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
using NoMercy.Encoder.BuildingBlocks;
using NoMercy.Encoder.Codecs;
using NoMercy.Encoder.Output;
using NoMercy.Encoder.Pipeline;

namespace NoMercy.Tests.Encoder.Output;

[Trait("Category", "Unit")]
public class PlaylistGeneratorAudioChannelsTests
{
    private static string Generate(params AudioOutputPlan[] audio)
    {
        OutputPlan plan = new(
            Format: OutputFormat.Hls,
            VideoOutputs:
            [
                new(
                    1920,
                    1080,
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
                ),
            ],
            AudioOutputs: audio,
            SubtitleOutputs: [],
            Thumbnails: null
        );

        Dictionary<string, VariantMetrics> videoMetrics = plan.VideoOutputs.ToDictionary(
            video => PlaylistGenerator.VideoVariantKey(video),
            _ => new VariantMetrics(5_000_000, 4_500_000)
        );
        Dictionary<string, VariantMetrics> audioMetrics = audio.ToDictionary(
            track => PlaylistGenerator.AudioVariantKey(track),
            _ => new VariantMetrics(192_000, 180_000)
        );

        return new PlaylistGenerator().GenerateMasterPlaylist(
            plan,
            "Example.Title",
            videoMetrics,
            audioMetrics
        );
    }

    private static string MediaLine(string master, string language) =>
        master
            .Split('\n')
            .Single(line =>
                line.StartsWith("#EXT-X-MEDIA:TYPE=AUDIO", StringComparison.Ordinal)
                && line.Contains($"LANGUAGE=\"{language}\"", StringComparison.Ordinal)
            );

    [Fact]
    public void GenerateMasterPlaylist_TranscodedAudio_WritesProfileChannels()
    {
        string master = Generate(
            new AudioOutputPlan("aac", 192, 6, 48000, StreamAction.Transcode, "eng", "0:a:0")
        );

        MediaLine(master, "eng").Should().Contain("CHANNELS=\"6\"");
    }

    [Fact]
    public void GenerateMasterPlaylist_CopiedAudio_WritesSourceChannelsNotProfileTarget()
    {
        string master = Generate(
            new AudioOutputPlan(
                "copy",
                0,
                2,
                48000,
                StreamAction.Copy,
                "jpn",
                "0:a:0",
                SourceCodecName: "eac3",
                SourceChannels: 6
            )
        );

        MediaLine(master, "jpn").Should().Contain("CHANNELS=\"6\"");
    }

    [Fact]
    public void GenerateMasterPlaylist_CopiedAudioOfUnknownChannels_OmitsTheAttribute()
    {
        string master = Generate(
            new AudioOutputPlan(
                "aac",
                0,
                2,
                48000,
                StreamAction.Copy,
                "eng",
                "0:a:0",
                SourceCodecName: "aac"
            )
        );

        MediaLine(master, "eng").Should().NotContain("CHANNELS=");
    }
}
