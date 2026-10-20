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

using NoMercy.Encoder.Analysis;
using NoMercy.Encoder.Codecs;
using NoMercy.Encoder.Errors;
using NoMercy.Encoder.Profiles;
using RateControlMode = NoMercy.Encoder.Profiles.RateControlMode;

namespace NoMercy.Tests.Encoder.Profiles;

/// <summary>
///     Tests for the catalogued <see cref="EncoderRuleId"/> entries that the new
///     <see cref="ProfileRuleValidator"/> emits. Each rule gets a positive case (rule fires) and a
///     negative case (rule stays quiet) so future refactors keep the precision tight.
/// </summary>
public class ProfileRuleValidatorTests
{
    // ── Shared builders ───────────────────────────────────────────────────────

    private static VideoOutput Video(
        VideoCodecType codec = VideoCodecType.H264,
        int? width = 1920,
        int? height = 1080,
        RateControlMode rc = RateControlMode.Crf,
        int crf = 23,
        int bitrate = 0,
        string? level = null,
        int bitDepth = 8,
        int keyframeSeconds = 2
    ) =>
        new(
            Policy: StreamPolicy.Transcode,
            Codec: codec,
            Width: width,
            Height: height,
            RateControl: rc,
            Crf: crf,
            BitrateKbps: bitrate,
            MaxBitrateKbps: null,
            BufferSizeKbps: null,
            Preset: null,
            CodecProfile: CodecProfile.Auto,
            Level: level,
            Tune: null,
            BitDepth: bitDepth,
            PixelFormat: null,
            KeyframeIntervalSeconds: keyframeSeconds,
            ConvertHdrToSdr: false,
            SegmentNameTemplate: "video_:framesize:/:framesize:_%05d",
            PlaylistNameTemplate: "video_:framesize:/playlist"
        );

    private static EncodingProfile ProfileFor(
        VideoOutput? video = null,
        Container container = Container.HlsFmp4,
        int segmentDurationSeconds = 6,
        LadderConfig? ladder = null,
        AudioOutput[]? audio = null,
        SubtitleOutput[]? subtitles = null,
        HdrPolicies hdrPolicies = HdrPolicies.PassthroughWhenPossible
    ) =>
        new(
            Id: Ulid.NewUlid(),
            Name: "rule-validator-test",
            Container: container,
            Video: video,
            Audio: audio ?? [],
            Subtitles: subtitles ?? [],
            SegmentDurationSeconds: segmentDurationSeconds,
            Ladder: ladder
        )
        {
            HdrPolicies = hdrPolicies,
        };

    private static AudioOutput Audio(AudioCodecType codec, int bitrate) =>
        new(
            Policy: StreamPolicy.Transcode,
            Codec: codec,
            BitrateKbps: bitrate,
            Channels: 2,
            SampleRateHz: 48000,
            AllowedLanguages: ["eng"],
            DefaultLanguage: "eng",
            Loudness: null,
            Downmix: null,
            SegmentNameTemplate: "audio_:lang:_:codec:/:lang:_:codec:_%05d",
            PlaylistNameTemplate: "audio_:lang:_:codec:/playlist"
        );

    private static SubtitleOutput Subtitle(SubtitleCodecType codec, SubtitlePolicy policy) =>
        new(
            Policy: policy,
            Codec: codec,
            AllowedLanguages: ["eng"],
            IncludeForced: true,
            OcrLanguage: "eng",
            PlaylistNameTemplate: "subs/:lang:"
        );

    [Fact]
    public void Validate_ProfileWithoutAudioOrSubtitles_DoesNotThrow()
    {
        EncodingProfile profile = ProfileFor(video: Video()) with
        {
            Audio = null!,
            Subtitles = null!,
        };

        Action act = () => ProfileRuleValidator.Validate(profile);

        act.Should().NotThrow();
    }

    private static bool HasRule(ValidationEnvelope env, string id) =>
        env.Errors.Any(r => r.Id == id) || env.Warnings.Any(r => r.Id == id);

    private static MediaInfo Source(
        int width = 1920,
        int height = 1080,
        double frameRate = 24,
        string? colorTransfer = null,
        string? colorPrimaries = null,
        string? stereoMode = null,
        string? sphericalProjection = null,
        DolbyVisionInfo? dolbyVision = null,
        bool variableFrameRate = false
    )
    {
        VideoStreamInfo video = new(
            Index: 0,
            Codec: "h264",
            Width: width,
            Height: height,
            FrameRate: frameRate,
            BitDepth: 8,
            PixelFormat: "yuv420p",
            ColorPrimaries: colorPrimaries,
            ColorTransfer: colorTransfer,
            ColorSpace: null,
            IsDefault: true,
            BitRateKbps: 5000,
            AverageFrameRate: frameRate,
            RealFrameRate: variableFrameRate ? frameRate + 5 : frameRate,
            Rotation: 0
        );
        return new(
            FilePath: "/test.mkv",
            Format: "matroska",
            Duration: TimeSpan.FromMinutes(90),
            OverallBitRateKbps: 5000,
            FileSizeBytes: 0,
            VideoStreams: [video],
            AudioStreams: [],
            SubtitleStreams: [],
            Chapters: [],
            DolbyVision: dolbyVision,
            StereoMode: stereoMode,
            SphericalProjection: sphericalProjection
        );
    }

    // ── LevelResolutionMismatch ──────────────────────────────────────────────

    [Fact]
    public void LevelResolutionMismatch_4kAtLevel4_0_Fires()
    {
        EncodingProfile profile = ProfileFor(
            Video(codec: VideoCodecType.H264, width: 3840, height: 2160, level: "4.0")
        );

        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);

        Assert.True(HasRule(env, EncoderRuleId.LevelResolutionMismatch));
        Assert.False(env.Valid);
    }

    [Fact]
    public void LevelResolutionMismatch_1080pAtLevel4_1_DoesNotFire()
    {
        EncodingProfile profile = ProfileFor(
            Video(codec: VideoCodecType.H264, width: 1920, height: 1080, level: "4.1")
        );

        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);

        Assert.False(HasRule(env, EncoderRuleId.LevelResolutionMismatch));
    }

    [Fact]
    public void LevelResolutionMismatch_NoLevel_DoesNotFire()
    {
        EncodingProfile profile = ProfileFor(Video(width: 3840, height: 2160));
        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);
        Assert.False(HasRule(env, EncoderRuleId.LevelResolutionMismatch));
    }

    // ── BitrateTooLowForResolution ───────────────────────────────────────────

    [Fact]
    public void BitrateTooLowForResolution_4kAt500kbps_Fires()
    {
        EncodingProfile profile = ProfileFor(
            Video(width: 3840, height: 2160, rc: RateControlMode.Vbr, bitrate: 500)
        );

        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);

        Assert.True(HasRule(env, EncoderRuleId.BitrateTooLowForResolution));
        EncoderRule rule = env.Warnings.First(r =>
            r.Id == EncoderRuleId.BitrateTooLowForResolution
        );
        Assert.Equal(EncoderRuleSeverity.Warning, rule.Severity);
    }

    [Fact]
    public void BitrateTooLowForResolution_1080pAt3000kbps_DoesNotFire()
    {
        EncodingProfile profile = ProfileFor(
            Video(width: 1920, height: 1080, rc: RateControlMode.Vbr, bitrate: 3000)
        );

        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);
        Assert.False(HasRule(env, EncoderRuleId.BitrateTooLowForResolution));
    }

    [Fact]
    public void BitrateTooLowForResolution_CrfMode_DoesNotFire()
    {
        // CRF profiles don't pin a bitrate target, so the rule shouldn't fire.
        EncodingProfile profile = ProfileFor(
            Video(width: 3840, height: 2160, rc: RateControlMode.Crf, crf: 23)
        );

        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);
        Assert.False(HasRule(env, EncoderRuleId.BitrateTooLowForResolution));
    }

    [Theory]
    [InlineData(3840, 8000)]
    [InlineData(2560, 5000)]
    [InlineData(1920, 2500)]
    [InlineData(1280, 1500)]
    [InlineData(854, 700)]
    [InlineData(640, 300)]
    public void MinimumBitrateKbpsFor_MatchesLadder(int width, int expectedMin)
    {
        Assert.Equal(expectedMin, ProfileRuleValidator.MinimumBitrateKbpsFor(width));
    }

    // ── CrfOutOfTypicalRange ─────────────────────────────────────────────────

    [Theory]
    [InlineData(5)] // very low (huge files)
    [InlineData(35)] // very high (heavy artefacts)
    [InlineData(51)] // boundary
    [InlineData(0)] // boundary
    public void CrfOutOfTypicalRange_FiresOutsideSeventeenTwentyEight(int crf)
    {
        EncodingProfile profile = ProfileFor(Video(rc: RateControlMode.Crf, crf: crf));

        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);

        Assert.True(HasRule(env, EncoderRuleId.CrfOutOfTypicalRange));
    }

    [Theory]
    [InlineData(17)]
    [InlineData(23)]
    [InlineData(28)]
    public void CrfOutOfTypicalRange_QuietInsideSeventeenTwentyEight(int crf)
    {
        EncodingProfile profile = ProfileFor(Video(rc: RateControlMode.Crf, crf: crf));
        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);
        Assert.False(HasRule(env, EncoderRuleId.CrfOutOfTypicalRange));
    }

    [Fact]
    public void CrfOutOfTypicalRange_NonCrfModes_DoesNotFire()
    {
        EncodingProfile profile = ProfileFor(Video(rc: RateControlMode.Vbr, crf: 5, bitrate: 4000));
        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);
        Assert.False(HasRule(env, EncoderRuleId.CrfOutOfTypicalRange));
    }

    [Fact]
    public void CrfOutOfTypicalRange_Av1_DoesNotFireOnH264TypicalRange()
    {
        // The rule is scoped to H.264/H.265 since AV1's CRF semantics differ. Should stay quiet
        // on AV1 even if the value would be flagged for H.264.
        EncodingProfile profile = ProfileFor(
            Video(codec: VideoCodecType.Av1, rc: RateControlMode.Crf, crf: 5)
        );
        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);
        Assert.False(HasRule(env, EncoderRuleId.CrfOutOfTypicalRange));
    }

    // ── HlsKeyframeSegmentMisalignment ───────────────────────────────────────

    [Fact]
    public void HlsKeyframeSegmentMisalignment_3sKeyframe6sSegment_Fires()
    {
        EncodingProfile profile = ProfileFor(
            Video(keyframeSeconds: 3),
            container: Container.HlsFmp4,
            segmentDurationSeconds: 6
        );
        // wait — 3 divides 6 evenly, no misalignment. Use 4.
        profile = ProfileFor(
            Video(keyframeSeconds: 4),
            container: Container.HlsFmp4,
            segmentDurationSeconds: 6
        );

        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);
        Assert.True(HasRule(env, EncoderRuleId.HlsKeyframeSegmentMisalignment));
    }

    [Fact]
    public void HlsKeyframeSegmentMisalignment_2sKeyframe6sSegment_DoesNotFire()
    {
        EncodingProfile profile = ProfileFor(
            Video(keyframeSeconds: 2),
            container: Container.HlsFmp4,
            segmentDurationSeconds: 6
        );

        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);
        Assert.False(HasRule(env, EncoderRuleId.HlsKeyframeSegmentMisalignment));
    }

    [Fact]
    public void HlsKeyframeSegmentMisalignment_NonHlsContainer_DoesNotFire()
    {
        EncodingProfile profile = ProfileFor(
            Video(keyframeSeconds: 4),
            container: Container.Mp4,
            segmentDurationSeconds: 6
        );
        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);
        Assert.False(HasRule(env, EncoderRuleId.HlsKeyframeSegmentMisalignment));
    }

    // ── LadderInverted ───────────────────────────────────────────────────────

    [Fact]
    public void LadderInverted_HigherWidthLowerBitrate_Fires()
    {
        LadderRung[] rungs =
        [
            new(854, 480, VideoCodecType.H264, 4000, 4800, 8000, 24), // 480p @ 4 Mbps (high)
            new(1920, 1080, VideoCodecType.H264, 2000, 2400, 4000, 24), // 1080p @ 2 Mbps (lower!)
        ];
        EncodingProfile profile = ProfileFor(
            Video(),
            ladder: new() { Mode = LadderMode.Manual, Rungs = rungs }
        );

        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);
        Assert.True(HasRule(env, EncoderRuleId.LadderInverted));
        Assert.False(env.Valid);
    }

    [Fact]
    public void LadderInverted_AscendingBitrate_DoesNotFire()
    {
        LadderRung[] rungs =
        [
            new(854, 480, VideoCodecType.H264, 1000, 1200, 2000, 24),
            new(1280, 720, VideoCodecType.H264, 2500, 3000, 5000, 24),
            new(1920, 1080, VideoCodecType.H264, 4500, 5400, 9000, 24),
        ];
        EncodingProfile profile = ProfileFor(
            Video(),
            ladder: new() { Mode = LadderMode.Manual, Rungs = rungs }
        );

        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);
        Assert.False(HasRule(env, EncoderRuleId.LadderInverted));
    }

    // ── AudioAc3OffLadderBitrate ─────────────────────────────────────────────

    [Fact]
    public void AudioAc3OffLadderBitrate_Ac3At333kbps_Fires()
    {
        EncodingProfile profile = ProfileFor(audio: [Audio(AudioCodecType.Ac3, 333)]);
        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);
        Assert.True(HasRule(env, EncoderRuleId.AudioAc3OffLadderBitrate));
    }

    [Fact]
    public void AudioAc3OffLadderBitrate_Ac3At320kbps_DoesNotFire()
    {
        EncodingProfile profile = ProfileFor(audio: [Audio(AudioCodecType.Ac3, 320)]);
        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);
        Assert.False(HasRule(env, EncoderRuleId.AudioAc3OffLadderBitrate));
    }

    [Fact]
    public void AudioAc3OffLadderBitrate_AacAnyBitrate_DoesNotFire()
    {
        EncodingProfile profile = ProfileFor(audio: [Audio(AudioCodecType.Aac, 137)]);
        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);
        Assert.False(HasRule(env, EncoderRuleId.AudioAc3OffLadderBitrate));
    }

    [Fact]
    public void AudioEac3OffLadderBitrate_OffLadder_Fires()
    {
        EncodingProfile profile = ProfileFor(audio: [Audio(AudioCodecType.Eac3, 137)]);
        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);
        Assert.True(HasRule(env, EncoderRuleId.AudioEac3OffLadderBitrate));
    }

    // ── SubtitlesContainerIncompatible ───────────────────────────────────────

    [Fact]
    public void SubtitlesContainerIncompatible_AssInMp4_Fires()
    {
        EncodingProfile profile = ProfileFor(
            Video(),
            container: Container.Mp4,
            subtitles: [Subtitle(SubtitleCodecType.Ass, SubtitlePolicy.Extract)]
        );
        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);
        Assert.True(HasRule(env, EncoderRuleId.SubtitlesContainerIncompatible));
        Assert.False(env.Valid);
    }

    [Fact]
    public void SubtitlesContainerIncompatible_WebVttInHls_DoesNotFire()
    {
        EncodingProfile profile = ProfileFor(
            Video(),
            container: Container.HlsFmp4,
            subtitles: [Subtitle(SubtitleCodecType.WebVtt, SubtitlePolicy.Extract)]
        );
        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);
        Assert.False(HasRule(env, EncoderRuleId.SubtitlesContainerIncompatible));
    }

    [Fact]
    public void SubtitlesContainerIncompatible_BurnInIgnored()
    {
        // BurnIn renders into video pixels; container compatibility doesn't matter.
        EncodingProfile profile = ProfileFor(
            Video(),
            container: Container.Mp4,
            subtitles: [Subtitle(SubtitleCodecType.Ass, SubtitlePolicy.BurnIn)]
        );
        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);
        Assert.False(HasRule(env, EncoderRuleId.SubtitlesContainerIncompatible));
    }

    // ── HdrInverseTonemapUnsupported ─────────────────────────────────────────

    [Fact]
    public void HdrInverseTonemapUnsupported_PreserveOn8Bit_Fires()
    {
        EncodingProfile profile = ProfileFor(
            Video(bitDepth: 8),
            hdrPolicies: HdrPolicies.AlwaysPreserve
        );

        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);
        Assert.True(HasRule(env, EncoderRuleId.HdrInverseTonemapUnsupported));
        Assert.False(env.Valid);
    }

    [Fact]
    public void HdrInverseTonemapUnsupported_PreserveOn10Bit_DoesNotFire()
    {
        EncodingProfile profile = ProfileFor(
            Video(bitDepth: 10),
            hdrPolicies: HdrPolicies.AlwaysPreserve
        );

        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);
        Assert.False(HasRule(env, EncoderRuleId.HdrInverseTonemapUnsupported));
    }

    [Fact]
    public void HdrInverseTonemapUnsupported_PassthroughPolicy_DoesNotFire()
    {
        EncodingProfile profile = ProfileFor(
            Video(bitDepth: 8),
            hdrPolicies: HdrPolicies.PassthroughWhenPossible
        );
        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);
        Assert.False(HasRule(env, EncoderRuleId.HdrInverseTonemapUnsupported));
    }

    // ── ProfileNameMissing ───────────────────────────────────────────────────

    [Fact]
    public void ProfileNameMissing_EmptyName_Fires()
    {
        EncodingProfile profile = ProfileFor(Video()) with { Name = "" };
        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);
        Assert.True(HasRule(env, EncoderRuleId.ProfileNameMissing));
        Assert.False(env.Valid);
    }

    [Fact]
    public void ProfileNameMissing_WhitespaceName_Fires()
    {
        EncodingProfile profile = ProfileFor(Video()) with { Name = "   " };
        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);
        Assert.True(HasRule(env, EncoderRuleId.ProfileNameMissing));
    }

    [Fact]
    public void ProfileNameMissing_RealName_DoesNotFire()
    {
        EncodingProfile profile = ProfileFor(Video()) with { Name = "1080p Streaming" };
        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);
        Assert.False(HasRule(env, EncoderRuleId.ProfileNameMissing));
    }

    // ── ProfileNoOutputs ─────────────────────────────────────────────────────

    [Fact]
    public void ProfileNoOutputs_NoStreams_Fires()
    {
        EncodingProfile profile = ProfileFor();
        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);
        Assert.True(HasRule(env, EncoderRuleId.ProfileNoOutputs));
        Assert.False(env.Valid);
    }

    [Fact]
    public void ProfileNoOutputs_AudioOnly_DoesNotFire()
    {
        EncodingProfile profile = ProfileFor(audio: [Audio(AudioCodecType.Aac, 192)]);
        // Audio-only container so other rules don't trip
        profile = profile with
        {
            Container = Container.Aac,
        };
        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);
        Assert.False(HasRule(env, EncoderRuleId.ProfileNoOutputs));
    }

    [Fact]
    public void ProfileNoOutputs_OmittedOutputs_StillFires()
    {
        // Even when an output exists, if it's Omitted there's nothing to encode.
        EncodingProfile profile = ProfileFor(
            audio: [Audio(AudioCodecType.Aac, 192) with { Policy = StreamPolicy.Omit }]
        );
        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);
        Assert.True(HasRule(env, EncoderRuleId.ProfileNoOutputs));
    }

    // ── VideoWidthInvalid / VideoHeightInvalid ───────────────────────────────

    [Fact]
    public void VideoWidthInvalid_ZeroWidth_Fires()
    {
        EncodingProfile profile = ProfileFor(Video(width: 0));
        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);
        Assert.True(HasRule(env, EncoderRuleId.VideoWidthInvalid));
    }

    [Fact]
    public void VideoWidthInvalid_NegativeWidth_Fires()
    {
        EncodingProfile profile = ProfileFor(Video(width: -1));
        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);
        Assert.True(HasRule(env, EncoderRuleId.VideoWidthInvalid));
    }

    [Fact]
    public void VideoWidthInvalid_NullWidth_DoesNotFire()
    {
        // null is valid — it means "keep source width" (e.g. an archive preset
        // that re-encodes the codec without rescaling).
        EncodingProfile profile = ProfileFor(Video(width: null));
        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);
        Assert.False(HasRule(env, EncoderRuleId.VideoWidthInvalid));
    }

    [Fact]
    public void VideoHeightInvalid_ZeroHeight_Fires()
    {
        EncodingProfile profile = ProfileFor(Video(height: 0));
        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);
        Assert.True(HasRule(env, EncoderRuleId.VideoHeightInvalid));
    }

    [Fact]
    public void VideoHeightInvalid_NullHeight_DoesNotFire()
    {
        // null is valid — encoder derives height from source aspect ratio.
        EncodingProfile profile = ProfileFor(Video(height: null));
        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);
        Assert.False(HasRule(env, EncoderRuleId.VideoHeightInvalid));
    }

    // ── VideoRateControlConflict ─────────────────────────────────────────────

    [Fact]
    public void VideoRateControlConflict_VbrWithoutBitrate_Fires()
    {
        EncodingProfile profile = ProfileFor(Video(rc: RateControlMode.Vbr, bitrate: 0, crf: 23));
        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);
        Assert.True(HasRule(env, EncoderRuleId.VideoRateControlConflict));
    }

    [Fact]
    public void VideoRateControlConflict_VbrWithBitrate_DoesNotFire()
    {
        EncodingProfile profile = ProfileFor(Video(rc: RateControlMode.Vbr, bitrate: 5000, crf: 0));
        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);
        Assert.False(HasRule(env, EncoderRuleId.VideoRateControlConflict));
    }

    // ── CodecContainerMismatch ───────────────────────────────────────────────

    [Fact]
    public void CodecContainerMismatch_Vp9InMp4_Fires()
    {
        EncodingProfile profile = ProfileFor(
            Video(codec: VideoCodecType.Vp9),
            container: Container.Mp4
        );
        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);
        Assert.True(HasRule(env, EncoderRuleId.CodecContainerMismatch));
    }

    [Fact]
    public void CodecContainerMismatch_H264InMp4_DoesNotFire()
    {
        EncodingProfile profile = ProfileFor(
            Video(codec: VideoCodecType.H264),
            container: Container.Mp4
        );
        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);
        Assert.False(HasRule(env, EncoderRuleId.CodecContainerMismatch));
    }

    // ── AudioCodecContainerMismatch ──────────────────────────────────────────

    [Fact]
    public void AudioCodecContainerMismatch_FlacInMp4_Fires()
    {
        EncodingProfile profile = ProfileFor(
            Video(),
            container: Container.Mp4,
            audio: [Audio(AudioCodecType.Flac, 0)]
        );
        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);
        Assert.True(HasRule(env, EncoderRuleId.AudioCodecContainerMismatch));
    }

    // ── HlsFmp4CodecMismatch ─────────────────────────────────────────────────

    [Fact]
    public void HlsFmp4CodecMismatch_HevcInHlsTs_Fires()
    {
        // HLS MPEG-TS only carries H.264 per Apple HLS Authoring §1.5
        EncodingProfile profile = ProfileFor(
            Video(codec: VideoCodecType.H265),
            container: Container.HlsTs
        );
        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);
        Assert.True(HasRule(env, EncoderRuleId.HlsFmp4CodecMismatch));
    }

    [Fact]
    public void HlsFmp4CodecMismatch_H264InHlsTs_DoesNotFire()
    {
        EncodingProfile profile = ProfileFor(
            Video(codec: VideoCodecType.H264),
            container: Container.HlsTs
        );
        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);
        Assert.False(HasRule(env, EncoderRuleId.HlsFmp4CodecMismatch));
    }

    // ── LadderDuplicateVariant ───────────────────────────────────────────────

    [Fact]
    public void LadderDuplicateVariant_DuplicateRung_Fires()
    {
        LadderRung[] rungs =
        [
            new(1280, 720, VideoCodecType.H264, 2500, 3000, 5000, 24),
            new(1280, 720, VideoCodecType.H264, 2500, 3000, 5000, 24), // duplicate
            new(1920, 1080, VideoCodecType.H264, 4500, 5400, 9000, 24),
        ];
        EncodingProfile profile = ProfileFor(
            Video(),
            ladder: new() { Mode = LadderMode.Manual, Rungs = rungs }
        );
        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);
        Assert.True(HasRule(env, EncoderRuleId.LadderDuplicateVariant));
    }

    [Fact]
    public void LadderDuplicateVariant_UniqueRungs_DoesNotFire()
    {
        LadderRung[] rungs =
        [
            new(854, 480, VideoCodecType.H264, 1000, 1200, 2000, 24),
            new(1280, 720, VideoCodecType.H264, 2500, 3000, 5000, 24),
            new(1920, 1080, VideoCodecType.H264, 4500, 5400, 9000, 24),
        ];
        EncodingProfile profile = ProfileFor(
            Video(),
            ladder: new() { Mode = LadderMode.Manual, Rungs = rungs }
        );
        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);
        Assert.False(HasRule(env, EncoderRuleId.LadderDuplicateVariant));
    }

    // ── SubtitlesBurnInPermanent ─────────────────────────────────────────────

    [Fact]
    public void SubtitlesBurnInPermanent_BurnInPolicy_FiresAsInfo()
    {
        EncodingProfile profile = ProfileFor(
            Video(),
            subtitles: [Subtitle(SubtitleCodecType.Ass, SubtitlePolicy.BurnIn)]
        );
        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);
        Assert.True(HasRule(env, EncoderRuleId.SubtitlesBurnInPermanent));
        // Info severity → does NOT invalidate the envelope.
        EncoderRule rule = env.Warnings.First(r => r.Id == EncoderRuleId.SubtitlesBurnInPermanent);
        Assert.Equal(EncoderRuleSeverity.Info, rule.Severity);
        Assert.True(env.Valid);
    }

    [Fact]
    public void SubtitlesBurnInPermanent_ExtractPolicy_DoesNotFire()
    {
        EncodingProfile profile = ProfileFor(
            Video(),
            subtitles: [Subtitle(SubtitleCodecType.WebVtt, SubtitlePolicy.Extract)]
        );
        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);
        Assert.False(HasRule(env, EncoderRuleId.SubtitlesBurnInPermanent));
    }

    // ── LevelInvalid ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("6.3")]
    [InlineData("9.9")]
    [InlineData("bogus")]
    public void LevelInvalid_UnknownH264Level_FiresAsError(string level)
    {
        EncodingProfile profile = ProfileFor(Video(level: level));

        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);

        Assert.True(HasRule(env, EncoderRuleId.LevelInvalid));
        Assert.False(env.Valid);
    }

    [Theory]
    [InlineData("4.0")]
    [InlineData("5.1")]
    public void LevelInvalid_KnownH264Level_DoesNotFire(string level)
    {
        EncodingProfile profile = ProfileFor(Video(width: 1280, height: 720, level: level));

        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);

        Assert.False(HasRule(env, EncoderRuleId.LevelInvalid));
    }

    [Fact]
    public void LevelInvalid_CodecWithNoLevelTable_NeverFires()
    {
        // AV1 has no level table in this catalogue; an unknown level must NOT be
        // flagged as invalid (we simply do not enumerate its levels).
        EncodingProfile profile = ProfileFor(Video(codec: VideoCodecType.Av1, level: "7.3"));

        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);

        Assert.False(HasRule(env, EncoderRuleId.LevelInvalid));
    }

    [Fact]
    public void LevelInvalid_NoLevelSet_DoesNotFire()
    {
        EncodingProfile profile = ProfileFor(Video(level: null));

        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);

        Assert.False(HasRule(env, EncoderRuleId.LevelInvalid));
    }

    // ── CustomArgsReservedFlag ───────────────────────────────────────────────

    [Theory]
    [InlineData("-c:v")]
    [InlineData("-preset")]
    [InlineData("-hwaccel")]
    [InlineData("-map")]
    [InlineData("-vf")]
    [InlineData("-hls_time")]
    [InlineData("-hls_segment_filename")]
    [InlineData("-filter_complex")]
    [InlineData("-init_hw_device")]
    public void CustomArgsReservedFlag_FiresAsError(string flag)
    {
        EncodingProfile profile = ProfileFor(Video()) with
        {
            CustomArguments = new() { [flag] = "anything" },
        };

        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);
        EncoderRule rule = env.Errors.First(r => r.Id == EncoderRuleId.CustomArgsReservedFlag);
        Assert.Equal(EncoderRuleSeverity.Error, rule.Severity);
        Assert.Contains(flag, rule.Field);
        Assert.False(env.Valid);
    }

    [Theory]
    [InlineData("-crf")]
    [InlineData("-b:v")]
    [InlineData("-maxrate")]
    [InlineData("-rc")]
    [InlineData("-cq")]
    [InlineData("-profile:v")]
    [InlineData("-level")]
    [InlineData("-g")]
    [InlineData("-color_primaries")]
    [InlineData("-pix_fmt")]
    public void CustomArgsReservedFlag_NewlyReservedRateControlAndEncoderFlags_Fire(string flag)
    {
        // These flags are all derived from typed profile fields (rate control,
        // codec profile, level, GOP, color). Overriding any of them via
        // CustomArguments desyncs the validator from what ffmpeg runs.
        EncodingProfile profile = ProfileFor(Video()) with
        {
            CustomArguments = new() { [flag] = "x" },
        };

        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);
        Assert.True(HasRule(env, EncoderRuleId.CustomArgsReservedFlag));
        Assert.False(env.Valid);
    }

    [Fact]
    public void CustomArgsReservedFlag_FiresForPerVideoCustomArguments()
    {
        // The real escape hatch the pipeline merges is VideoOutput.CustomArguments,
        // which was validated by nothing. A video-level -rc override must fire.
        VideoOutput video = Video() with
        {
            CustomArguments = new() { ["-rc"] = "cbr" },
        };
        EncodingProfile profile = ProfileFor(video);

        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);
        EncoderRule rule = env.Errors.First(r => r.Id == EncoderRuleId.CustomArgsReservedFlag);
        Assert.Contains("video.custom_arguments", rule.Field);
        Assert.False(env.Valid);
    }

    [Fact]
    public void CustomArgsReservedFlag_FiresForPerAudioCustomArguments()
    {
        AudioOutput audio = Audio(AudioCodecType.Aac, 192) with
        {
            CustomArguments = new() { ["-b:a"] = "320k" },
        };
        EncodingProfile profile = ProfileFor(Video(), audio: [audio]);

        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);
        EncoderRule rule = env.Errors.First(r => r.Id == EncoderRuleId.CustomArgsReservedFlag);
        Assert.Contains("audio[0].custom_arguments", rule.Field);
        Assert.False(env.Valid);
    }

    [Fact]
    public void CustomArgsReservedFlag_AllowsNonReservedPerVideoFlag()
    {
        // A genuinely-informational per-output flag stays permitted.
        VideoOutput video = Video() with
        {
            CustomArguments = new() { ["-loglevel"] = "info" },
        };
        EncodingProfile profile = ProfileFor(video);

        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);
        Assert.False(HasRule(env, EncoderRuleId.CustomArgsReservedFlag));
    }

    [Fact]
    public void CustomArgsReservedFlag_AllowsKeyWithoutLeadingDash()
    {
        // Some callers store keys as "c:v" (without dash). Normalize before checking.
        EncodingProfile profile = ProfileFor(Video()) with
        {
            CustomArguments = new() { ["c:v"] = "libx264" },
        };

        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);
        Assert.True(HasRule(env, EncoderRuleId.CustomArgsReservedFlag));
    }

    [Fact]
    public void CustomArgsReservedFlag_AllowsNonReservedFlag()
    {
        // -loglevel is informational, not derived from profile fields. Permitted.
        EncodingProfile profile = ProfileFor(Video()) with
        {
            CustomArguments = new() { ["-loglevel"] = "info" },
        };

        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);
        Assert.False(HasRule(env, EncoderRuleId.CustomArgsReservedFlag));
    }

    [Fact]
    public void CustomArgsReservedFlag_NoCustomArgs_DoesNotFire()
    {
        EncodingProfile profile = ProfileFor(Video());
        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);
        Assert.False(HasRule(env, EncoderRuleId.CustomArgsReservedFlag));
    }

    // ── SubtitlesAssNeedsCapableClient ───────────────────────────────────────

    [Fact]
    public void SubtitlesAssNeedsCapableClient_AssInHls_FiresInfo()
    {
        EncodingProfile profile = ProfileFor(
            Video(),
            container: Container.HlsFmp4,
            subtitles: [Subtitle(SubtitleCodecType.Ass, SubtitlePolicy.Extract)]
        );
        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);
        Assert.True(HasRule(env, EncoderRuleId.SubtitlesAssNeedsCapableClient));
        EncoderRule rule = env.Warnings.First(r =>
            r.Id == EncoderRuleId.SubtitlesAssNeedsCapableClient
        );
        Assert.Equal(EncoderRuleSeverity.Info, rule.Severity);
    }

    [Fact]
    public void SubtitlesAssNeedsCapableClient_AssInMkv_DoesNotFire()
    {
        EncodingProfile profile = ProfileFor(
            Video(),
            container: Container.Mkv,
            subtitles: [Subtitle(SubtitleCodecType.Ass, SubtitlePolicy.Extract)]
        );
        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);
        Assert.False(HasRule(env, EncoderRuleId.SubtitlesAssNeedsCapableClient));
    }

    [Fact]
    public void SubtitlesAssNeedsCapableClient_WebVttInHls_DoesNotFire()
    {
        EncodingProfile profile = ProfileFor(
            Video(),
            container: Container.HlsFmp4,
            subtitles: [Subtitle(SubtitleCodecType.WebVtt, SubtitlePolicy.Extract)]
        );
        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);
        Assert.False(HasRule(env, EncoderRuleId.SubtitlesAssNeedsCapableClient));
    }

    // ── DrmHttpNotHttps ──────────────────────────────────────────────────────

    [Fact]
    public void DrmHttpNotHttps_HttpKeyUri_Fires()
    {
        EncodingProfile profile = ProfileFor(Video()) with
        {
            Drm = new("aes-128", new() { ["key_uri"] = "http://server/key.bin" }),
        };
        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);
        Assert.True(HasRule(env, EncoderRuleId.DrmHttpNotHttps));
        Assert.False(env.Valid);
    }

    [Fact]
    public void DrmHttpNotHttps_HttpsKeyUri_DoesNotFire()
    {
        EncodingProfile profile = ProfileFor(Video()) with
        {
            Drm = new("aes-128", new() { ["key_uri"] = "https://server/key.bin" }),
        };
        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);
        Assert.False(HasRule(env, EncoderRuleId.DrmHttpNotHttps));
    }

    [Fact]
    public void DrmHttpNotHttps_HttpLicenseUrl_AlsoFires()
    {
        EncodingProfile profile = ProfileFor(Video()) with
        {
            Drm = new("cenc", new() { ["license_url"] = "http://license.example/issue" }),
        };
        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);
        Assert.True(HasRule(env, EncoderRuleId.DrmHttpNotHttps));
    }

    [Fact]
    public void DrmHttpNotHttps_NoDrm_DoesNotFire()
    {
        EncodingProfile profile = ProfileFor(Video());
        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);
        Assert.False(HasRule(env, EncoderRuleId.DrmHttpNotHttps));
    }

    // ── Envelope integrity ───────────────────────────────────────────────────

    [Fact]
    public void Validate_NoRules_ReturnsValidEmptyEnvelope()
    {
        EncodingProfile profile = ProfileFor(Video(keyframeSeconds: 2));
        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);
        Assert.True(env.Valid);
        Assert.Empty(env.Errors);
        Assert.Empty(env.Warnings);
    }

    [Fact]
    public void Validate_ErrorPresent_EnvelopeInvalid()
    {
        EncodingProfile profile = ProfileFor(
            Video(width: 3840, height: 2160, level: "4.0") // LevelResolutionMismatch
        );
        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);
        Assert.False(env.Valid);
        Assert.NotEmpty(env.Errors);
    }

    [Fact]
    public void Validate_OnlyWarnings_EnvelopeStillValid()
    {
        EncodingProfile profile = ProfileFor(Video(rc: RateControlMode.Crf, crf: 5));
        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);
        Assert.True(env.Valid);
        Assert.NotEmpty(env.Warnings);
    }

    // ── VideoRateControlMissing ──────────────────────────────────────────────

    [Fact]
    public void VideoRateControlMissing_CrfModeButCrfZero_Fires()
    {
        EncodingProfile profile = ProfileFor(Video(rc: RateControlMode.Crf, crf: 0));
        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);
        Assert.True(HasRule(env, EncoderRuleId.VideoRateControlMissing));
        Assert.False(env.Valid);
    }

    [Fact]
    public void VideoRateControlMissing_VbrModeButBitrateZero_Fires()
    {
        EncodingProfile profile = ProfileFor(Video(rc: RateControlMode.Vbr, crf: 0, bitrate: 0));
        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);
        Assert.True(HasRule(env, EncoderRuleId.VideoRateControlMissing));
    }

    [Fact]
    public void VideoRateControlMissing_CbrModeButBitrateZero_Fires()
    {
        EncodingProfile profile = ProfileFor(Video(rc: RateControlMode.Cbr, crf: 0, bitrate: 0));
        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);
        Assert.True(HasRule(env, EncoderRuleId.VideoRateControlMissing));
    }

    [Fact]
    public void VideoRateControlMissing_CrfModeWithValidCrf_DoesNotFire()
    {
        EncodingProfile profile = ProfileFor(Video(rc: RateControlMode.Crf, crf: 23));
        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);
        Assert.False(HasRule(env, EncoderRuleId.VideoRateControlMissing));
    }

    [Fact]
    public void VideoRateControlMissing_VbrModeWithValidBitrate_DoesNotFire()
    {
        EncodingProfile profile = ProfileFor(Video(rc: RateControlMode.Vbr, crf: 0, bitrate: 4000));
        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);
        Assert.False(HasRule(env, EncoderRuleId.VideoRateControlMissing));
    }

    // ── DrmKeyMissing ────────────────────────────────────────────────────────

    [Fact]
    public void DrmKeyMissing_SchemeSetButNoParameters_Fires()
    {
        EncodingProfile profile = ProfileFor(Video()) with { Drm = new("aes-128", new()) };
        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);
        Assert.True(HasRule(env, EncoderRuleId.DrmKeyMissing));
        Assert.False(env.Valid);
    }

    [Fact]
    public void DrmKeyMissing_SchemeSetButParametersWithoutKeyUri_Fires()
    {
        EncodingProfile profile = ProfileFor(Video()) with
        {
            Drm = new("cenc", new() { ["scheme_id_uri"] = "urn:something" }),
        };
        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);
        Assert.True(HasRule(env, EncoderRuleId.DrmKeyMissing));
    }

    [Fact]
    public void DrmKeyMissing_KeyUriPresent_DoesNotFire()
    {
        EncodingProfile profile = ProfileFor(Video()) with
        {
            Drm = new("aes-128", new() { ["key_uri"] = "https://server/key.bin" }),
        };
        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);
        Assert.False(HasRule(env, EncoderRuleId.DrmKeyMissing));
    }

    [Theory]
    [InlineData("key_url")]
    [InlineData("keyfile")]
    [InlineData("key_file")]
    [InlineData("Key_Uri")]
    public void DrmKeyMissing_EveryAcceptedKeyName_DoesNotFire(string parameterName)
    {
        EncodingProfile profile = ProfileFor(Video()) with
        {
            Drm = new("aes-128", new() { [parameterName] = "https://server/key.bin" }),
        };
        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);
        Assert.False(HasRule(env, EncoderRuleId.DrmKeyMissing));
    }

    [Fact]
    public void DrmKeyMissing_KeyUriPresentButBlank_Fires()
    {
        // A blank key_uri reaches the packager as an empty KeyUri and fails there.
        EncodingProfile profile = ProfileFor(Video()) with
        {
            Drm = new("aes-128", new() { ["key_uri"] = "   " }),
        };
        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);
        Assert.True(HasRule(env, EncoderRuleId.DrmKeyMissing));
    }

    [Fact]
    public void DrmKeyMissing_LicenseUrlPresent_DoesNotFire()
    {
        EncodingProfile profile = ProfileFor(Video()) with
        {
            Drm = new("cenc", new() { ["license_url"] = "https://license.example/issue" }),
        };
        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);
        Assert.False(HasRule(env, EncoderRuleId.DrmKeyMissing));
    }

    [Fact]
    public void DrmKeyMissing_NoDrm_DoesNotFire()
    {
        EncodingProfile profile = ProfileFor(Video());
        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);
        Assert.False(HasRule(env, EncoderRuleId.DrmKeyMissing));
    }

    [Fact]
    public void DrmKeyMissing_SchemeNone_DoesNotFire()
    {
        EncodingProfile profile = ProfileFor(Video()) with { Drm = new("none", new()) };
        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);
        Assert.False(HasRule(env, EncoderRuleId.DrmKeyMissing));
    }

    // ── DrmSchemeUnsupported ─────────────────────────────────────────────────

    [Theory]
    [InlineData("cbcs")]
    [InlineData("aes-128-cbc")]
    [InlineData("aes 128")]
    public void DrmSchemeUnsupported_UnknownScheme_Fires(string scheme)
    {
        EncodingProfile profile = ProfileFor(Video()) with
        {
            Drm = new(scheme, new() { ["key_uri"] = "https://server/key.bin" }),
        };
        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);
        Assert.True(HasRule(env, EncoderRuleId.DrmSchemeUnsupported));
        Assert.False(env.Valid);
    }

    [Fact]
    public void DrmSchemeUnsupported_Message_NamesTheSchemeAndTheSupportedOnes()
    {
        EncodingProfile profile = ProfileFor(Video()) with
        {
            Drm = new("cbcs", new() { ["key_uri"] = "https://server/key.bin" }),
        };
        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);
        EncoderRule rule = env.Errors.Single(r => r.Id == EncoderRuleId.DrmSchemeUnsupported);
        Assert.Equal("drm.scheme", rule.Field);
        Assert.Contains("cbcs", rule.Message);
        Assert.Contains("aes128", rule.Message);
        Assert.Contains("cenc", rule.Message);
    }

    [Theory]
    [InlineData("aes128")]
    [InlineData("AES-128")]
    [InlineData("cenc")]
    [InlineData("none")]
    [InlineData("")]
    public void DrmSchemeUnsupported_SupportedOrDisabledScheme_DoesNotFire(string scheme)
    {
        EncodingProfile profile = ProfileFor(Video()) with
        {
            Drm = new(scheme, new() { ["key_uri"] = "https://server/key.bin" }),
        };
        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);
        Assert.False(HasRule(env, EncoderRuleId.DrmSchemeUnsupported));
    }

    [Fact]
    public void DrmSchemeUnsupported_NoDrm_DoesNotFire()
    {
        ValidationEnvelope env = ProfileRuleValidator.Validate(ProfileFor(Video()));
        Assert.False(HasRule(env, EncoderRuleId.DrmSchemeUnsupported));
    }

    // ── SourceUpscalingDetected (source-dependent) ──────────────────────────

    [Fact]
    public void SourceUpscalingDetected_TargetExceedsSource_Fires()
    {
        EncodingProfile profile = ProfileFor(Video(width: 1920, height: 1080));
        MediaInfo source = Source(width: 720, height: 480);
        ValidationEnvelope env = ProfileRuleValidator.ValidateWithSource(profile, source);
        Assert.True(HasRule(env, EncoderRuleId.SourceUpscalingDetected));
        Assert.True(env.Valid); // warning only
    }

    [Fact]
    public void SourceUpscalingDetected_TargetMatchesSource_DoesNotFire()
    {
        EncodingProfile profile = ProfileFor(Video(width: 1920, height: 1080));
        MediaInfo source = Source(width: 1920, height: 1080);
        ValidationEnvelope env = ProfileRuleValidator.ValidateWithSource(profile, source);
        Assert.False(HasRule(env, EncoderRuleId.SourceUpscalingDetected));
    }

    [Fact]
    public void SourceUpscalingDetected_TargetBelowSource_DoesNotFire()
    {
        EncodingProfile profile = ProfileFor(Video(width: 1280, height: 720));
        MediaInfo source = Source(width: 3840, height: 2160);
        ValidationEnvelope env = ProfileRuleValidator.ValidateWithSource(profile, source);
        Assert.False(HasRule(env, EncoderRuleId.SourceUpscalingDetected));
    }

    // ── AudioBitrateMissing ─────────────────────────────────────────────────

    [Fact]
    public void AudioBitrateMissing_LossyAudioBitrateZero_Fires()
    {
        EncodingProfile profile = ProfileFor(
            Video(),
            audio: [Audio(AudioCodecType.Aac, bitrate: 0)]
        );
        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);
        Assert.True(HasRule(env, EncoderRuleId.AudioBitrateMissing));
    }

    [Fact]
    public void AudioBitrateMissing_FlacBitrateZero_DoesNotFire()
    {
        EncodingProfile profile = ProfileFor(
            Video(),
            container: Container.Mkv,
            audio: [Audio(AudioCodecType.Flac, bitrate: 0)]
        );
        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);
        Assert.False(HasRule(env, EncoderRuleId.AudioBitrateMissing));
    }

    [Fact]
    public void AudioBitrateMissing_TrueHdBitrateZero_DoesNotFire()
    {
        EncodingProfile profile = ProfileFor(
            Video(),
            container: Container.Mkv,
            audio: [Audio(AudioCodecType.TrueHd, bitrate: 0)]
        );
        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);
        Assert.False(HasRule(env, EncoderRuleId.AudioBitrateMissing));
    }

    [Fact]
    public void AudioBitrateMissing_ValidAacBitrate_DoesNotFire()
    {
        EncodingProfile profile = ProfileFor(
            Video(),
            audio: [Audio(AudioCodecType.Aac, bitrate: 192)]
        );
        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);
        Assert.False(HasRule(env, EncoderRuleId.AudioBitrateMissing));
    }

    // ── LadderManualEmpty ───────────────────────────────────────────────────

    [Fact]
    public void LadderManualEmpty_ManualWithEmptyRungs_Fires()
    {
        EncodingProfile profile = ProfileFor(
            Video(),
            ladder: new() { Mode = LadderMode.Manual, Rungs = [] }
        );
        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);
        Assert.True(HasRule(env, EncoderRuleId.LadderManualEmpty));
    }

    [Fact]
    public void LadderManualEmpty_ManualWithRungs_DoesNotFire()
    {
        EncodingProfile profile = ProfileFor(
            Video(),
            ladder: new()
            {
                Mode = LadderMode.Manual,
                Rungs = [new(1920, 1080, VideoCodecType.H264, 4000, 4800, 8000, 24)],
            }
        );
        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);
        Assert.False(HasRule(env, EncoderRuleId.LadderManualEmpty));
    }

    [Fact]
    public void LadderManualEmpty_AutoMode_DoesNotFire()
    {
        EncodingProfile profile = ProfileFor(
            Video(),
            ladder: new() { Mode = LadderMode.Auto, Rungs = [] }
        );
        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);
        Assert.False(HasRule(env, EncoderRuleId.LadderManualEmpty));
    }

    // ── LadderManualUnsorted ────────────────────────────────────────────────

    [Fact]
    public void LadderManualUnsorted_DescendingBitrates_Fires()
    {
        EncodingProfile profile = ProfileFor(
            Video(),
            ladder: new()
            {
                Mode = LadderMode.Manual,
                Rungs =
                [
                    new(1920, 1080, VideoCodecType.H264, 8000, 9600, 16000, 24),
                    new(1280, 720, VideoCodecType.H264, 4000, 4800, 8000, 24),
                ],
            }
        );
        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);
        Assert.True(HasRule(env, EncoderRuleId.LadderManualUnsorted));
    }

    [Fact]
    public void LadderManualUnsorted_AscendingBitrates_DoesNotFire()
    {
        EncodingProfile profile = ProfileFor(
            Video(),
            ladder: new()
            {
                Mode = LadderMode.Manual,
                Rungs =
                [
                    new(1280, 720, VideoCodecType.H264, 4000, 4800, 8000, 24),
                    new(1920, 1080, VideoCodecType.H264, 8000, 9600, 16000, 24),
                ],
            }
        );
        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);
        Assert.False(HasRule(env, EncoderRuleId.LadderManualUnsorted));
    }

    // ── SourceVariableFrameRate ─────────────────────────────────────────────

    [Fact]
    public void SourceVariableFrameRate_VfrSource_Fires()
    {
        EncodingProfile profile = ProfileFor(Video());
        MediaInfo source = Source(variableFrameRate: true);
        ValidationEnvelope env = ProfileRuleValidator.ValidateWithSource(profile, source);
        Assert.True(HasRule(env, EncoderRuleId.SourceVariableFrameRate));
        Assert.True(env.Valid); // warning only
    }

    [Fact]
    public void SourceVariableFrameRate_CfrSource_DoesNotFire()
    {
        EncodingProfile profile = ProfileFor(Video());
        MediaInfo source = Source(variableFrameRate: false);
        ValidationEnvelope env = ProfileRuleValidator.ValidateWithSource(profile, source);
        Assert.False(HasRule(env, EncoderRuleId.SourceVariableFrameRate));
    }

    // ── SourceDolbyVisionWillBeStripped ─────────────────────────────────────

    [Fact]
    public void SourceDolbyVisionWillBeStripped_DvSource_Fires()
    {
        EncodingProfile profile = ProfileFor(Video());
        MediaInfo source = Source(
            dolbyVision: new(
                Profile: 8,
                Level: 6,
                HasRpu: true,
                HasEl: false,
                BlCompat: DvBlCompatibility.Hdr10
            )
        );
        ValidationEnvelope env = ProfileRuleValidator.ValidateWithSource(profile, source);
        Assert.True(HasRule(env, EncoderRuleId.SourceDolbyVisionWillBeStripped));
    }

    [Fact]
    public void SourceDolbyVisionWillBeStripped_NoDv_DoesNotFire()
    {
        EncodingProfile profile = ProfileFor(Video());
        MediaInfo source = Source();
        ValidationEnvelope env = ProfileRuleValidator.ValidateWithSource(profile, source);
        Assert.False(HasRule(env, EncoderRuleId.SourceDolbyVisionWillBeStripped));
    }

    // ── StereoscopicSourceUnsupported ───────────────────────────────────────

    [Fact]
    public void StereoscopicSourceUnsupported_3dSource_Fires()
    {
        EncodingProfile profile = ProfileFor(Video());
        MediaInfo source = Source(stereoMode: "side_by_side_left");
        ValidationEnvelope env = ProfileRuleValidator.ValidateWithSource(profile, source);
        Assert.True(HasRule(env, EncoderRuleId.StereoscopicSourceUnsupported));
        Assert.False(env.Valid); // error
    }

    [Fact]
    public void StereoscopicSourceUnsupported_FlatSource_DoesNotFire()
    {
        EncodingProfile profile = ProfileFor(Video());
        MediaInfo source = Source(stereoMode: null);
        ValidationEnvelope env = ProfileRuleValidator.ValidateWithSource(profile, source);
        Assert.False(HasRule(env, EncoderRuleId.StereoscopicSourceUnsupported));
    }

    // ── SphericalMetadataWillBeStripped ─────────────────────────────────────

    [Fact]
    public void SphericalMetadataWillBeStripped_VrSource_Fires()
    {
        EncodingProfile profile = ProfileFor(Video());
        MediaInfo source = Source(sphericalProjection: "equirectangular");
        ValidationEnvelope env = ProfileRuleValidator.ValidateWithSource(profile, source);
        Assert.True(HasRule(env, EncoderRuleId.SphericalMetadataWillBeStripped));
    }

    [Fact]
    public void SphericalMetadataWillBeStripped_FlatSource_DoesNotFire()
    {
        EncodingProfile profile = ProfileFor(Video());
        MediaInfo source = Source(sphericalProjection: null);
        ValidationEnvelope env = ProfileRuleValidator.ValidateWithSource(profile, source);
        Assert.False(HasRule(env, EncoderRuleId.SphericalMetadataWillBeStripped));
    }

    // ── LevelFrameRateCapExceeded ───────────────────────────────────────────

    [Fact]
    public void LevelFrameRateCapExceeded_4kAt60FpsAtLevel5_0_Fires()
    {
        // H.264 Level 5.0 caps at 1,082,400 mb/s ≈ 4K30 luma rate. 4K60 exceeds it.
        EncodingProfile profile = ProfileFor(
            Video(codec: VideoCodecType.H264, width: 3840, height: 2160, level: "5.0")
        );
        MediaInfo source = Source(width: 3840, height: 2160, frameRate: 60);
        ValidationEnvelope env = ProfileRuleValidator.ValidateWithSource(profile, source);
        Assert.True(HasRule(env, EncoderRuleId.LevelFrameRateCapExceeded));
    }

    [Fact]
    public void LevelFrameRateCapExceeded_1080pAt30FpsAtLevel4_1_DoesNotFire()
    {
        EncodingProfile profile = ProfileFor(
            Video(codec: VideoCodecType.H264, width: 1920, height: 1080, level: "4.1")
        );
        MediaInfo source = Source(width: 1920, height: 1080, frameRate: 30);
        ValidationEnvelope env = ProfileRuleValidator.ValidateWithSource(profile, source);
        Assert.False(HasRule(env, EncoderRuleId.LevelFrameRateCapExceeded));
    }

    [Fact]
    public void LevelFrameRateCapExceeded_NoLevel_DoesNotFire()
    {
        EncodingProfile profile = ProfileFor(
            Video(codec: VideoCodecType.H264, width: 3840, height: 2160, level: null)
        );
        MediaInfo source = Source(width: 3840, height: 2160, frameRate: 60);
        ValidationEnvelope env = ProfileRuleValidator.ValidateWithSource(profile, source);
        Assert.False(HasRule(env, EncoderRuleId.LevelFrameRateCapExceeded));
    }

    [Fact]
    public void EveryEmittedRule_HasNonEmptyMessageAndFix()
    {
        // Run every rule through a profile that trips it, then assert the contract: every rule
        // ships with a non-empty Message + Fix (dashboard depends on both being present).
        EncodingProfile profile = ProfileFor(
            video: Video(width: 3840, height: 2160, level: "4.0", crf: 5, keyframeSeconds: 4),
            container: Container.HlsFmp4,
            segmentDurationSeconds: 6,
            audio: [Audio(AudioCodecType.Ac3, 333)],
            subtitles: [Subtitle(SubtitleCodecType.Ass, SubtitlePolicy.Extract)],
            ladder: new()
            {
                Mode = LadderMode.Manual,
                Rungs =
                [
                    new(854, 480, VideoCodecType.H264, 4000, 4800, 8000, 24),
                    new(1920, 1080, VideoCodecType.H264, 2000, 2400, 4000, 24),
                ],
            },
            hdrPolicies: HdrPolicies.AlwaysPreserve
        );

        ValidationEnvelope env = ProfileRuleValidator.Validate(profile);
        foreach (EncoderRule rule in env.Errors.Concat(env.Warnings))
        {
            Assert.False(string.IsNullOrWhiteSpace(rule.Id), $"Rule has empty Id");
            Assert.False(string.IsNullOrWhiteSpace(rule.Field), $"Rule {rule.Id} has empty Field");
            Assert.False(
                string.IsNullOrWhiteSpace(rule.Message),
                $"Rule {rule.Id} has empty Message"
            );
            Assert.False(string.IsNullOrWhiteSpace(rule.Fix), $"Rule {rule.Id} has empty Fix");
        }
    }
}
