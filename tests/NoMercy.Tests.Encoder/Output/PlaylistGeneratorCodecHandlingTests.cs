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

public class PlaylistGeneratorCodecHandlingTests
{
    private const string MediaTitle = "Test.Title";

    private string Generate(OutputPlan plan)
    {
        Dictionary<string, VariantMetrics> videoMetrics = plan.VideoOutputs.ToDictionary(
            v => VideoVariantKey(v),
            _ => new VariantMetrics(5_000_000, 4_500_000)
        );

        Dictionary<string, VariantMetrics> audioMetrics = plan.AudioOutputs.ToDictionary(
            a => AudioVariantKey(a),
            _ => new VariantMetrics(192_000, 180_000)
        );

        PlaylistGenerator generator = new();
        return generator.GenerateMasterPlaylist(plan, MediaTitle, videoMetrics, audioMetrics);
    }

    private static string VideoVariantKey(VideoOutputPlan video) =>
        TemplateResolver.Resolve(
            video.PlaylistNameTemplate,
            TemplateResolver.VideoTokens(video.Width, video.Height, video.IsHdrOutput)
        );

    private static string AudioVariantKey(AudioOutputPlan audio) =>
        TemplateResolver.Resolve(
            audio.PlaylistNameTemplate,
            TemplateResolver.AudioTokens(audio.Language ?? "und", audio.CodecToken, audio.Channels)
        );

    [Fact]
    public void GenerateMasterPlaylist_H264VideoOnly_OmitsCodecsAttribute()
    {
        OutputPlan plan = new(
            Format: OutputFormat.Hls,
            VideoOutputs:
            [
                new(
                    1920, 1080, "libx264", 23, 8000, "medium", "high", "4.0", false,
                    "yuv420p", "[v0]", new()
                ),
            ],
            AudioOutputs: [],
            SubtitleOutputs: [],
            Thumbnails: null
        );

        string master = Generate(plan);

        master.Should().Contain("avc1.640028");
        master.Should().NotContain("CODECS=\"\"");
    }

    [Fact]
    public void GenerateMasterPlaylist_HevcVideoOnly_IncludesHevcCodec()
    {
        OutputPlan plan = new(
            Format: OutputFormat.Hls,
            VideoOutputs:
            [
                new(
                    1920, 1080, "libx265", 23, 8000, "medium", "main", "4.0", false,
                    "yuv420p", "[v0]", new()
                ),
            ],
            AudioOutputs: [],
            SubtitleOutputs: [],
            Thumbnails: null
        );

        string master = Generate(plan);

        master.Should().Contain("hvc1.");
    }

    [Fact]
    public void GenerateMasterPlaylist_Av1VideoOnly_IncludesAv1Codec()
    {
        OutputPlan plan = new(
            Format: OutputFormat.Hls,
            VideoOutputs:
            [
                new(
                    1920, 1080, "libsvtav1", 23, 8000, "medium", null, "4.0", false,
                    "yuv420p", "[v0]", new()
                ),
            ],
            AudioOutputs: [],
            SubtitleOutputs: [],
            Thumbnails: null
        );

        string master = Generate(plan);

        master.Should().Contain("av01.");
    }

    [Fact]
    public void GenerateMasterPlaylist_VideoAndAudio_CombinesCodecStrings()
    {
        OutputPlan plan = new(
            Format: OutputFormat.Hls,
            VideoOutputs:
            [
                new(
                    1920, 1080, "libx264", 23, 8000, "medium", "high", "4.0", false,
                    "yuv420p", "[v0]", new()
                ),
            ],
            AudioOutputs:
            [
                new("aac", 192, 2, 48000, StreamAction.Transcode, "eng", "0:a:0"),
            ],
            SubtitleOutputs: [],
            Thumbnails: null
        );

        string master = Generate(plan);

        master.Should().Contain("CODECS=\"avc1.640028,mp4a.40.2\"");
    }

    [Fact]
    public void GenerateMasterPlaylist_CopyModeVideo_OmitsCodecTag()
    {
        OutputPlan plan = new(
            Format: OutputFormat.Hls,
            VideoOutputs:
            [
                new(
                    1920, 1080, "copy", 23, 0, null, null, null, false,
                    "yuv420p", "[v0]", new()
                ),
            ],
            AudioOutputs:
            [
                new("aac", 192, 2, 48000, StreamAction.Transcode, "eng", "0:a:0"),
            ],
            SubtitleOutputs: [],
            Thumbnails: null
        );

        string master = Generate(plan);

        master.Should().NotContain("CODECS=");
        master.Should().NotContain("avc1");
        master.Should().NotContain("hvc1");
        master.Should().NotContain("av01");
    }

    [Fact]
    public void GenerateMasterPlaylist_CopyModeAudioOnly_OmitsCodecTag()
    {
        OutputPlan plan = new(
            Format: OutputFormat.Hls,
            VideoOutputs:
            [
                new(
                    1920, 1080, "libx264", 23, 8000, "medium", "high", "4.0", false,
                    "yuv420p", "[v0]", new()
                ),
            ],
            AudioOutputs:
            [
                new("copy", 0, 2, 48000, StreamAction.Copy, "eng", "0:a:0"),
            ],
            SubtitleOutputs: [],
            Thumbnails: null
        );

        string master = Generate(plan);

        master.Should().Contain("CODECS=\"avc1.640028\"");
        master.Should().NotContain("mp4a");
        master.Should().NotContain("opus");
    }

    [Fact]
    public void GenerateMasterPlaylist_AudioCodecsVary_EachVariantShowsItsCodec()
    {
        PlaylistGenerator generator = new();
        OutputPlan plan = new(
            Format: OutputFormat.Hls,
            VideoOutputs:
            [
                new(
                    1920, 1080, "libx264", 23, 8000, "medium", "high", "4.0", false,
                    "yuv420p", "[v0]", new()
                ),
            ],
            AudioOutputs:
            [
                new("aac", 192, 2, 48000, StreamAction.Transcode, "eng", "0:a:0"),
            ],
            SubtitleOutputs: [],
            Thumbnails: null
        );

        Dictionary<string, VariantMetrics> videoMetrics = plan.VideoOutputs.ToDictionary(
            v => VideoVariantKey(v),
            _ => new VariantMetrics(5_000_000, 4_500_000)
        );

        Dictionary<string, VariantMetrics> audioMetrics = plan.AudioOutputs.ToDictionary(
            a => AudioVariantKey(a),
            _ => new VariantMetrics(192_000, 180_000)
        );

        string master = generator.GenerateMasterPlaylist(plan, MediaTitle, videoMetrics, audioMetrics);

        master.Should().Contain("mp4a.40.2");
    }

    [Fact]
    public void GenerateMasterPlaylist_TenBitHevc_IncludesBitDepthInCodec()
    {
        OutputPlan plan = new(
            Format: OutputFormat.Hls,
            VideoOutputs:
            [
                new(
                    1920, 1080, "libx265", 23, 8000, "medium", "main10", "4.1", true,
                    "yuv420p10le", "[v0]", new()
                ),
            ],
            AudioOutputs: [],
            SubtitleOutputs: [],
            Thumbnails: null
        );

        string master = Generate(plan);

        master.Should().Contain("hvc1.");
        master.Should().NotContain("avc1");
    }

    [Fact]
    public void GenerateMasterPlaylist_MultipleVideoResolutions_BandwidthsDistinct()
    {
        OutputPlan plan = new(
            Format: OutputFormat.Hls,
            VideoOutputs:
            [
                new(
                    1920, 1080, "libx264", 23, 8000, "medium", "high", "4.0", false,
                    "yuv420p", "[v0]", new()
                ),
                new(
                    1280, 720, "libx264", 23, 4000, "medium", "high", "3.1", false,
                    "yuv420p", "[v1]", new()
                ),
            ],
            AudioOutputs:
            [
                new("aac", 192, 2, 48000, StreamAction.Transcode, "eng", "0:a:0"),
            ],
            SubtitleOutputs: [],
            Thumbnails: null
        );

        Dictionary<string, VariantMetrics> videoMetrics = new()
        {
            [VideoVariantKey(plan.VideoOutputs[0])] = new(8_000_000, 6_500_000),
            [VideoVariantKey(plan.VideoOutputs[1])] = new(3_000_000, 2_400_000),
        };

        Dictionary<string, VariantMetrics> audioMetrics = plan.AudioOutputs.ToDictionary(
            a => AudioVariantKey(a),
            _ => new VariantMetrics(192_000, 180_000)
        );

        PlaylistGenerator generator = new();
        string master = generator.GenerateMasterPlaylist(plan, MediaTitle, videoMetrics, audioMetrics);

        master.Should().Contain("BANDWIDTH=8192000");
        master.Should().Contain("BANDWIDTH=3192000");
    }
}
