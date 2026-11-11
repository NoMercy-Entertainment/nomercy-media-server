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

/// <summary>A copied video has no known codec, so a variant must not list audio alone.</summary>
public class HlsCopiedVideoCodecsTests
{
    private const string BrokenLine =
        "#EXT-X-STREAM-INF:BANDWIDTH=100,RESOLUTION=1920x800,FRAME-RATE=23.976,CODECS=\"mp4a.40.2\",VIDEO-RANGE=SDR,AUDIO=\"audio_aac\"";

    private static string Master(string streamInf) =>
        $"#EXTM3U\r\n{streamInf}\r\nvideo_1920x800_SDR/video_1920x800_SDR.m3u8\r\n";

    private static VideoOutputPlan Video(string encoder) =>
        new(1920, 800, encoder, 23, 8000, "medium", "high", "4.0", false, "yuv420p", "[v0]", new());

    private static string Generate(string videoEncoder)
    {
        OutputPlan plan = new(
            Format: OutputFormat.Hls,
            VideoOutputs: [Video(videoEncoder)],
            AudioOutputs:
            [
                new("aac", 192, 2, 48000, StreamAction.Transcode, "eng", "0:a:0"),
            ],
            SubtitleOutputs: [],
            Thumbnails: null
        );
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

    [Fact]
    public void A_copied_video_variant_lists_no_codecs()
    {
        Assert.DoesNotContain("CODECS=", Generate("copy"), StringComparison.Ordinal);
    }

    [Fact]
    public void An_encoded_video_variant_lists_both_codecs()
    {
        Assert.Contains("CODECS=\"avc1.640028,mp4a.40.2\"", Generate("libx264"), StringComparison.Ordinal);
    }

    [Fact]
    public void Repair_drops_an_audio_only_codecs_attribute_and_keeps_the_rest()
    {
        string repaired = HlsMasterAudioOnlyCodecsRepair.DropAudioOnlyCodecs(Master(BrokenLine));

        Assert.DoesNotContain("CODECS=", repaired, StringComparison.Ordinal);
        Assert.Contains("FRAME-RATE=23.976,VIDEO-RANGE=SDR,AUDIO=\"audio_aac\"\r\n", repaired, StringComparison.Ordinal);
    }

    [Fact]
    public void Repair_leaves_a_master_with_a_video_codec_alone()
    {
        string master = Master("#EXT-X-STREAM-INF:BANDWIDTH=100,RESOLUTION=1920x800,CODECS=\"avc1.640028,mp4a.40.2\"");

        Assert.Same(master, HlsMasterAudioOnlyCodecsRepair.DropAudioOnlyCodecs(master));
    }

    [Fact]
    public void Repair_leaves_a_real_audio_only_variant_alone()
    {
        string master = Master("#EXT-X-STREAM-INF:BANDWIDTH=100,CODECS=\"mp4a.40.2\"");

        Assert.Same(master, HlsMasterAudioOnlyCodecsRepair.DropAudioOnlyCodecs(master));
    }
}
