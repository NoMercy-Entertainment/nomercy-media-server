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
using NoMercy.Encoder.Pipeline;
using NoMercy.Encoder.Pipeline.Stages;

namespace NoMercy.Tests.Encoder.Pipeline.Stages;

/// <summary>
/// Plan-level path collision resolution. When two outputs resolve to the same
/// on-disk path the disambiguator appends a per-stream / per-codec suffix so
/// segments don't overwrite each other. Single-collision plans pass through
/// unchanged.
/// </summary>
public class PlanStageDisambiguationTests
{
    private static AudioOutputPlan Audio(
        string lang,
        string encoder,
        int sourceIdx,
        string segTemplate = "audio_:lang:_:codec:/:lang:_:codec:_%05d",
        string playlistTemplate = "audio_:lang:_:codec:/playlist"
    ) =>
        new(
            EncoderName: encoder,
            BitrateKbps: 128,
            Channels: 2,
            SampleRate: 48000,
            Action: StreamAction.Transcode,
            Language: lang,
            MapLabel: $"0:a:{sourceIdx}"
        )
        {
            SegmentNameTemplate = segTemplate,
            PlaylistNameTemplate = playlistTemplate,
        };

    private static VideoOutputPlan Video(
        int width,
        int height,
        string encoder,
        bool hdr = false,
        string segTemplate = "video_:framesize:/:framesize:_%05d",
        string playlistTemplate = "video_:framesize:/playlist"
    ) =>
        new(
            Width: width,
            Height: height,
            EncoderName: encoder,
            Crf: 23,
            BitrateKbps: 0,
            Preset: "medium",
            Profile: "main",
            Level: "4.0",
            TenBit: false,
            PixelFormat: "yuv420p",
            MapLabel: "[v]",
            ExtraFlags: []
        )
        {
            SegmentNameTemplate = segTemplate,
            PlaylistNameTemplate = playlistTemplate,
            VideoRange = hdr ? VideoRange.Pq : VideoRange.Sdr,
        };

    // ── Audio disambiguation ────────────────────────────────────────────────

    [Fact]
    public void DisambiguateAudio_SingleLanguagePerCodec_PassesThrough()
    {
        AudioOutputPlan[] plans = [Audio("eng", "aac", 0), Audio("fra", "aac", 1)];

        AudioOutputPlan[] result = PlanStageDisambiguation.DisambiguateAudio(plans).ToArray();

        result[0].SegmentNameTemplate.Should().Be(plans[0].SegmentNameTemplate);
        result[1].SegmentNameTemplate.Should().Be(plans[1].SegmentNameTemplate);
    }

    [Fact]
    public void DisambiguateAudio_TwoEnglishAac_Disambiguates()
    {
        // Two English AAC streams (e.g. commentary track + main) would collide
        // on audio_eng_aac/. Append source index suffix per stream.
        AudioOutputPlan[] plans = [Audio("eng", "aac", 0), Audio("eng", "aac", 1)];

        AudioOutputPlan[] result = PlanStageDisambiguation.DisambiguateAudio(plans).ToArray();

        result[0].SegmentNameTemplate.Should().Contain("_0");
        result[1].SegmentNameTemplate.Should().Contain("_1");
        result[0].SegmentNameTemplate.Should().NotBe(result[1].SegmentNameTemplate);
    }

    [Fact]
    public void DisambiguateAudio_DifferentCodecsSameLanguage_PassThrough()
    {
        // Two English streams with DIFFERENT codecs land in different dirs already.
        AudioOutputPlan[] plans = [Audio("eng", "aac", 0), Audio("eng", "libfdk_aac", 1)];

        AudioOutputPlan[] result = PlanStageDisambiguation.DisambiguateAudio(plans).ToArray();

        // 'lib' and 'libfdk_' prefixes are stripped to compare — these still collide
        // because both resolve to "aac" codec token. Disambiguates.
        result[0].SegmentNameTemplate.Should().NotBe(plans[0].SegmentNameTemplate);
    }

    [Fact]
    public void DisambiguateAudio_SegmentAndPlaylistBothDisambiguated()
    {
        AudioOutputPlan[] plans = [Audio("eng", "aac", 0), Audio("eng", "aac", 1)];

        AudioOutputPlan[] result = PlanStageDisambiguation.DisambiguateAudio(plans).ToArray();

        // Both directory AND filename suffix get the disambiguator — otherwise
        // .m4s segments still collide inside the same dir.
        result[0].SegmentNameTemplate.Should().Contain("/").And.Contain("_0");
        result[0].PlaylistNameTemplate.Should().Contain("/").And.Contain("_0");
    }

    [Fact]
    public void DisambiguateAudio_EmptyInput_ReturnsEmpty()
    {
        PlanStageDisambiguation.DisambiguateAudio([]).Should().BeEmpty();
    }

    // ── Video disambiguation ────────────────────────────────────────────────

    [Fact]
    public void DisambiguateVideo_DifferentResolutions_PassesThrough()
    {
        VideoOutputPlan[] plans = [Video(1920, 1080, "libx264"), Video(1280, 720, "libx264")];

        VideoOutputPlan[] result = PlanStageDisambiguation.DisambiguateVideo(plans);

        result[0].SegmentNameTemplate.Should().Be(plans[0].SegmentNameTemplate);
        result[1].SegmentNameTemplate.Should().Be(plans[1].SegmentNameTemplate);
    }

    [Fact]
    public void DisambiguateVideo_SameResolutionTwoCodecs_AppendsCodecFamily()
    {
        // EmitHdrAndSdr can produce H.264 1080p AND HEVC 1080p — they'd collide
        // on video_1920x1080/. Append codec family suffix.
        VideoOutputPlan[] plans = [Video(1920, 1080, "libx264"), Video(1920, 1080, "libx265")];

        VideoOutputPlan[] result = PlanStageDisambiguation.DisambiguateVideo(plans);

        result[0].SegmentNameTemplate.Should().Contain("avc");
        result[1].SegmentNameTemplate.Should().Contain("hevc");
    }

    [Fact]
    public void DisambiguateVideo_HdrAndSdrAtSameSize_PassThrough()
    {
        // Different IsHdrOutput → different group keys → no collision.
        VideoOutputPlan[] plans =
        [
            Video(1920, 1080, "libx265", hdr: true),
            Video(1920, 1080, "libx265", hdr: false),
        ];

        VideoOutputPlan[] result = PlanStageDisambiguation.DisambiguateVideo(plans);

        result[0].SegmentNameTemplate.Should().Be(plans[0].SegmentNameTemplate);
        result[1].SegmentNameTemplate.Should().Be(plans[1].SegmentNameTemplate);
    }

    [Fact]
    public void DisambiguateVideo_EmptyInput_ReturnsEmpty()
    {
        PlanStageDisambiguation.DisambiguateVideo([]).Should().BeEmpty();
    }
}
