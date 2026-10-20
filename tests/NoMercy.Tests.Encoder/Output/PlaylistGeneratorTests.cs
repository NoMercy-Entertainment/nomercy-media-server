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

public class PlaylistGeneratorTests
{
    private const string MediaTitle = "Movie.Name.NoMercy";

    private string Generate(OutputPlan plan)
    {
        // GenerateMasterPlaylist now skips variants whose measured bandwidth
        // is zero (the analyzer's signal for "playlist / segments missing on
        // disk") to keep dead variants out of the published master. Tests
        // that build a synthetic OutputPlan need to seed non-zero metrics so
        // the variant rows still render — otherwise the master is empty and
        // every codec-tag / structure assertion fails.
        // Metrics are keyed by each variant's resolved playlist path (NOT
        // MapLabel — every rung re-plans as "[v0]", so MapLabel keys collide and
        // collapse the ladder onto one shared BANDWIDTH). Mirror the production
        // keying so the lookup in GenerateMasterPlaylist resolves.
        Dictionary<string, VariantMetrics> videoMetrics = plan.VideoOutputs.ToDictionary(
            VideoVariantKey,
            _ => new VariantMetrics(PeakBandwidth: 5_000_000, AverageBandwidth: 4_500_000)
        );

        Dictionary<string, VariantMetrics> audioMetrics = plan.AudioOutputs.ToDictionary(
            AudioVariantKey,
            _ => new VariantMetrics(PeakBandwidth: 192_000, AverageBandwidth: 180_000)
        );

        PlaylistGenerator generator = new();
        return generator.GenerateMasterPlaylist(plan, MediaTitle, videoMetrics, audioMetrics);
    }

    // The metrics-dict key a variant is stored/looked-up under: its resolved
    // playlist path, unique per resolution/HDR. Mirrors HlsOutputStrategy +
    // PlaylistGenerator exactly.
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
    public void MasterPlaylist_ContainsExtm3u()
    {
        string playlist = Generate(CreatePlan());

        playlist.Should().StartWith("#EXTM3U");
        // Version is computed from active features. Basic mpegts with no subtitles
        // group, no fMP4, and no chapter date-ranges requires version 3.
        playlist.Should().Contain("#EXT-X-VERSION:3");
    }

    [Fact]
    public void MasterPlaylist_ContainsIndependentSegments()
    {
        string playlist = Generate(CreatePlan());

        playlist.Should().Contain("#EXT-X-INDEPENDENT-SEGMENTS");
    }

    [Fact]
    public void MasterPlaylist_ContainsVideoVariants()
    {
        string playlist = Generate(CreateMultiResPlan());

        playlist.Should().Contain("RESOLUTION=1920x1080");
        playlist.Should().Contain("RESOLUTION=1280x720");
        playlist.Should().Contain("video_1920x1080_SDR/video_1920x1080_SDR.m3u8");
        playlist.Should().Contain("video_1280x720_SDR/video_1280x720_SDR.m3u8");
    }

    [Fact]
    public void MasterPlaylist_H264_CorrectCodecTag()
    {
        string playlist = Generate(CreatePlan());

        playlist.Should().Contain("avc1.640028");
    }

    [Fact]
    public void MasterPlaylist_Hevc_CorrectCodecTag()
    {
        string playlist = Generate(CreatePlan(encoderName: "hevc_nvenc"));

        playlist.Should().Contain("hvc1.");
    }

    [Fact]
    public void MasterPlaylist_Av1_10bit_CorrectCodecTag()
    {
        // Plan fixture declares Level="4.0" (Av1 spec table A.1 → index 8)
        // and tenBit=true → expect av01.0.08M.10. Phase 4.17 introduced the
        // spec-accurate HlsCodecsStringBuilder which derives the level index
        // from the plan instead of hard-coding 5.3 (index 15) like the legacy
        // generator did.
        string playlist = Generate(CreatePlan(encoderName: "libsvtav1", tenBit: true));

        playlist.Should().Contain("av01.0.08M.10");
    }

    [Fact]
    public void MasterPlaylist_CopyVideo_OmitsCodecsInsteadOfLying()
    {
        // "copy" is a passthrough — the real codec could be anything. The
        // master must never advertise avc1 (the old H.264-fallback bug) for
        // a copy-mode variant; CODECS should list only the known audio codec.
        string playlist = Generate(CreatePlan(encoderName: "copy"));

        playlist.Should().NotContain("avc1.");
        playlist.Should().Contain("CODECS=\"mp4a.40.2\"");
    }

    [Fact]
    public void MasterPlaylist_AudioGroup_Present()
    {
        string playlist = Generate(CreatePlan());

        playlist.Should().Contain("#EXT-X-MEDIA:TYPE=AUDIO");
        playlist.Should().Contain("GROUP-ID=\"audio_aac\"");
        playlist.Should().Contain("LANGUAGE=\"eng\"");
    }

    [Fact]
    public void MasterPlaylist_AacAudio_Mp4aCodecTag()
    {
        string playlist = Generate(CreatePlan());

        playlist.Should().Contain("mp4a.40.2");
    }

    // ── ComputeMasterVersion (internal helper) ─────────────────────────────

    [Theory]
    [InlineData(false, false, false, 3)] // mpegts baseline → v3
    [InlineData(true, false, false, 6)] // subs → v6
    [InlineData(false, true, false, 7)] // fmp4 → v7
    [InlineData(true, true, false, 7)] // subs + fmp4 → v7
    [InlineData(false, false, true, 8)] // chapter date-ranges → v8
    [InlineData(true, true, true, 8)] // everything → v8
    public void ComputeMasterVersion_ReturnsCorrectMinVersion(
        bool hasSubsGroup,
        bool hasFmp4,
        bool hasChapterDateRanges,
        int expectedVersion
    )
    {
        // Pin the EXT-X-VERSION ladder. Wrong version makes players fall
        // back to a more conservative interpretation or refuse to parse.
        // Reflection because the method is internal — keep the contract
        // testable without exposing it publicly to the rest of the encoder.
        int actual = (int)
            typeof(PlaylistGenerator)
                .GetMethod(
                    "ComputeMasterVersion",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic
                )!
                .Invoke(null, [hasSubsGroup, hasFmp4, hasChapterDateRanges])!;

        actual.Should().Be(expectedVersion);
    }

    [Fact]
    public void MasterPlaylist_Fmp4SegmentType_BumpsVersionTo7()
    {
        // fMP4 requires v7 minimum per HLS spec.
        OutputPlan plan = CreatePlan() with
        {
            HlsOptions = new() { SegmentType = "fmp4" },
        };

        string playlist = Generate(plan);

        playlist.Should().Contain("#EXT-X-VERSION:7");
        // fMP4 forces INDEPENDENT-SEGMENTS regardless of option.
        playlist.Should().Contain("#EXT-X-INDEPENDENT-SEGMENTS");
    }

    [Fact]
    public void MasterPlaylist_AudioCopyAction_Included()
    {
        // Copy is a valid audio action that still produces a sidecar HLS
        // group — the master MUST list it the same as a transcoded variant.
        OutputPlan plan = CreatePlan() with
        {
            AudioOutputs = [new("aac", 0, 2, 48000, StreamAction.Copy, "eng", "0:a:0")],
        };

        string playlist = Generate(plan);

        playlist.Should().Contain("#EXT-X-MEDIA:TYPE=AUDIO");
        playlist.Should().Contain("LANGUAGE=\"eng\"");
    }

    [Fact]
    public void MasterPlaylist_AudioWithZeroBandwidth_IsSkipped()
    {
        // The analyzer reports 0 bandwidth when segments are missing on
        // disk. Listing those variants in the master makes hls.js / VLC
        // bail on the first fetch.
        PlaylistGenerator generator = new();
        OutputPlan plan = CreatePlan();
        Dictionary<string, VariantMetrics> vidMetrics = new()
        {
            [VideoVariantKey(plan.VideoOutputs[0])] = new(5_000_000, 3_500_000),
        };
        Dictionary<string, VariantMetrics> audMetrics = new()
        {
            [AudioVariantKey(plan.AudioOutputs[0])] = new(0, 0),
        };

        string playlist = generator.GenerateMasterPlaylist(
            plan,
            MediaTitle,
            vidMetrics,
            audMetrics
        );

        playlist.Should().NotContain("#EXT-X-MEDIA:TYPE=AUDIO");
    }

    [Fact]
    public void MasterPlaylist_VideoWithZeroBandwidth_IsSkipped()
    {
        // Same reason for video — a missing variant playlist must never
        // ship in the master.
        PlaylistGenerator generator = new();
        OutputPlan plan = CreatePlan();
        Dictionary<string, VariantMetrics> vidMetrics = new()
        {
            [VideoVariantKey(plan.VideoOutputs[0])] = new(0, 0),
        };
        Dictionary<string, VariantMetrics> audMetrics = new()
        {
            [AudioVariantKey(plan.AudioOutputs[0])] = new(192_000, 180_000),
        };

        string playlist = generator.GenerateMasterPlaylist(
            plan,
            MediaTitle,
            vidMetrics,
            audMetrics
        );

        playlist.Should().NotContain("RESOLUTION=");
    }

    [Fact]
    public void MasterPlaylist_OpusAudio_CodecTag()
    {
        OutputPlan plan = CreatePlan() with
        {
            AudioOutputs = [new("libopus", 128, 2, 48000, StreamAction.Transcode, "eng", "0:a:0")],
        };

        string playlist = Generate(plan);

        playlist.Should().Contain("opus");
    }

    [Fact]
    public void MasterPlaylist_Eac3Audio_CodecTag()
    {
        OutputPlan plan = CreatePlan() with
        {
            AudioOutputs = [new("eac3", 384, 6, 48000, StreamAction.Transcode, "eng", "0:a:0")],
        };

        string playlist = Generate(plan);

        playlist.Should().Contain("ec-3");
    }

    [Fact]
    public void MasterPlaylist_MeasuredBandwidth_UsedWhenProvided()
    {
        PlaylistGenerator generator = new();
        OutputPlan plan = CreatePlan();
        Dictionary<string, VariantMetrics> vidMetrics = new()
        {
            [VideoVariantKey(plan.VideoOutputs[0])] = new(5_000_000, 3_500_000),
        };
        Dictionary<string, VariantMetrics> audMetrics = new()
        {
            [AudioVariantKey(plan.AudioOutputs[0])] = new(256_000, 192_000),
        };

        string playlist = generator.GenerateMasterPlaylist(
            plan,
            MediaTitle,
            vidMetrics,
            audMetrics
        );

        playlist.Should().Contain("BANDWIDTH=5256000");
        playlist.Should().Contain("AVERAGE-BANDWIDTH=3692000");
    }

    [Fact]
    public void MasterPlaylist_VariantsSharingMapLabel_EachGetTheirOwnBandwidth()
    {
        // Regression for the identical-BANDWIDTH bug: every rung re-plans in its
        // own bundle as MapLabel "[v0]", so two variants can carry the SAME
        // MapLabel at different resolutions. Metrics are keyed by the resolved
        // playlist PATH, so each variant must still advertise its OWN measured
        // bandwidth — not one value shared across the whole ladder (which is what
        // MapLabel keying produced).
        OutputPlan plan = CreateMultiResPlan() with
        { };
        plan = plan with
        {
            VideoOutputs =
            [
                plan.VideoOutputs[0] with
                {
                    MapLabel = "[v0]",
                }, // 1080p
                plan.VideoOutputs[1] with
                {
                    MapLabel = "[v0]",
                }, // 720p, SAME label
            ],
        };

        Dictionary<string, VariantMetrics> vidMetrics = new()
        {
            [VideoVariantKey(plan.VideoOutputs[0])] = new(8_000_000, 6_000_000),
            [VideoVariantKey(plan.VideoOutputs[1])] = new(3_000_000, 2_400_000),
        };
        Dictionary<string, VariantMetrics> audMetrics = new()
        {
            [AudioVariantKey(plan.AudioOutputs[0])] = new(192_000, 180_000),
        };

        string playlist = new PlaylistGenerator().GenerateMasterPlaylist(
            plan,
            MediaTitle,
            vidMetrics,
            audMetrics
        );

        // Each variant advertises its own bandwidth (video + audio), proving the
        // shared-MapLabel collision no longer collapses the ladder.
        playlist.Should().Contain("BANDWIDTH=8192000");
        playlist.Should().Contain("BANDWIDTH=3192000");
    }

    [Theory]
    [InlineData("arib-std-b67", "VIDEO-RANGE=HLG")]
    [InlineData("smpte2084", "VIDEO-RANGE=PQ")]
    [InlineData(null, "VIDEO-RANGE=PQ")]
    public void MasterPlaylist_HdrVideoRange_FollowsTheColorTransfer(
        string? colorTransfer,
        string expected
    )
    {
        OutputPlan plan = CreatePlan(encoderName: "libx265", tenBit: true);
        plan = plan with
        {
            VideoOutputs =
            [
                plan.VideoOutputs[0] with { IsHdrOutput = true, ColorTransfer = colorTransfer },
            ],
        };

        string playlist = Generate(plan);

        playlist.Should().Contain(expected);
    }

    private static OutputPlan CreatePlan(string encoderName = "libx264", bool tenBit = false)
    {
        return new(
            Format: OutputFormat.Hls,
            VideoOutputs:
            [
                new(
                    1920,
                    1080,
                    encoderName,
                    23,
                    8000,
                    "medium",
                    "high",
                    "4.0",
                    tenBit,
                    tenBit ? "yuv420p10le" : "yuv420p",
                    "[v0]",
                    new()
                ),
            ],
            AudioOutputs: [new("aac", 192, 2, 48000, StreamAction.Transcode, "eng", "0:a:0")],
            SubtitleOutputs: [],
            Thumbnails: null
        );
    }

    private static OutputPlan CreateMultiResPlan()
    {
        return new(
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
                new(
                    1280,
                    720,
                    "libx264",
                    23,
                    4000,
                    "medium",
                    "high",
                    "3.1",
                    false,
                    "yuv420p",
                    "[v1]",
                    new()
                ),
            ],
            AudioOutputs: [new("aac", 192, 2, 48000, StreamAction.Transcode, "eng", "0:a:0")],
            SubtitleOutputs: [],
            Thumbnails: null
        );
    }
}
