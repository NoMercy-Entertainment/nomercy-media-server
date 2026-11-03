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

using NoMercy.Encoder.Codecs;
using NoMercy.Encoder.Errors;
using NoMercy.Encoder.Profiles;
using RateControlMode = NoMercy.Encoder.Profiles.RateControlMode;

namespace NoMercy.Tests.Encoder.Profiles;

/// <summary>
///     The unsafe-flag rule refuses custom ffmpeg arguments that read or write local files
///     or reach the network. One case group per category, over all four argument sources.
/// </summary>
public class CustomArgsUnsafeFlagTests
{
    private static VideoOutput Video() =>
        new(
            Policy: StreamPolicy.Transcode,
            Codec: VideoCodecType.H264,
            Width: 1920,
            Height: 1080,
            RateControl: RateControlMode.Crf,
            Crf: 23,
            BitrateKbps: 0,
            MaxBitrateKbps: null,
            BufferSizeKbps: null,
            Preset: null,
            CodecProfile: CodecProfile.Auto,
            Level: null,
            Tune: null,
            BitDepth: 8,
            PixelFormat: null,
            KeyframeIntervalSeconds: 2,
            ConvertHdrToSdr: false,
            SegmentNameTemplate: "video_:framesize:/:framesize:_%05d",
            PlaylistNameTemplate: "video_:framesize:/playlist"
        );

    private static AudioOutput Audio() =>
        new(
            Policy: StreamPolicy.Transcode,
            Codec: AudioCodecType.Aac,
            BitrateKbps: 192,
            Channels: 2,
            SampleRateHz: 48000,
            AllowedLanguages: ["eng"],
            DefaultLanguage: "eng",
            Loudness: null,
            Downmix: null,
            SegmentNameTemplate: "audio_:lang:_:codec:/:lang:_:codec:_%05d",
            PlaylistNameTemplate: "audio_:lang:_:codec:/playlist"
        );

    private static SubtitleOutput Subtitle() =>
        new(
            Policy: SubtitlePolicy.Extract,
            Codec: SubtitleCodecType.WebVtt,
            AllowedLanguages: ["eng"],
            IncludeForced: true,
            OcrLanguage: "eng",
            PlaylistNameTemplate: "subs/:lang:"
        );

    private static EncodingProfile ProfileWith(
        Dictionary<string, string>? profileArgs = null,
        Dictionary<string, string>? videoArgs = null,
        Dictionary<string, string>? audioArgs = null,
        Dictionary<string, string>? subtitleArgs = null
    ) =>
        new(
            Id: Ulid.NewUlid(),
            Name: "unsafe-flag-test",
            Container: Container.HlsFmp4,
            Video: Video() with
            {
                CustomArguments = videoArgs,
            },
            Audio: [Audio() with { CustomArguments = audioArgs }],
            Subtitles: [Subtitle() with { CustomArguments = subtitleArgs }],
            SegmentDurationSeconds: 6,
            Ladder: null
        )
        {
            CustomArguments = profileArgs,
        };

    private static bool Fires(EncodingProfile profile, string? fieldContains = null) =>
        ProfileRuleValidator
            .Validate(profile)
            .Errors.Any(r =>
                r.Id == EncoderRuleId.CustomArgsUnsafeFlag
                && r.Severity == EncoderRuleSeverity.Error
                && (fieldContains is null || r.Field.Contains(fieldContains))
            );

    // ── Category: flags that read or write files or open extra inputs ────────

    [Theory]
    [InlineData("-i")]
    [InlineData("-progress")]
    [InlineData("-vstats_file")]
    [InlineData("-vstats")]
    [InlineData("-report")]
    [InlineData("-dump_attachment")]
    [InlineData("-dump_attachment:t")]
    [InlineData("-attach")]
    [InlineData("-filter_script")]
    [InlineData("-filter_script:v")]
    [InlineData("-filter_complex_script")]
    [InlineData("-lavfi")]
    [InlineData("-protocol_whitelist")]
    [InlineData("-fpre")]
    [InlineData("-vpre")]
    [InlineData("-sdp_file")]
    [InlineData("-/filter")]
    [InlineData("-/filter:v")]
    [InlineData("-/filter_complex")]
    public void DeniedFlagKey_Fires(string key)
    {
        Assert.True(Fires(ProfileWith(profileArgs: new() { [key] = "x" })));
    }

    [Theory]
    [InlineData("i")]
    [InlineData("progress")]
    [InlineData("vstats_file")]
    [InlineData("-I")]
    [InlineData("-PROGRESS")]
    public void DeniedFlagKey_FiresWithoutDashAndInAnyCase(string key)
    {
        Assert.True(Fires(ProfileWith(profileArgs: new() { [key] = "x" })));
    }

    // ── Category: filter text that opens files ───────────────────────────────

    [Theory]
    [InlineData("movie=/tmp/example.mkv")]
    [InlineData("amovie=/tmp/example.mka")]
    [InlineData("subtitles=/tmp/example.srt")]
    [InlineData("ass=/tmp/example.ass")]
    [InlineData("textfile=/tmp/example.txt")]
    [InlineData("file=/tmp/example.txt")]
    [InlineData("scale=1280:720,movie=example.mkv[b];[0:v][b]overlay")]
    [InlineData("drawtext=fontfile=example.ttf:text=hi")]
    [InlineData("lut3d=file=example.cube")]
    public void FilterSourceValue_Fires(string value)
    {
        Assert.True(Fires(ProfileWith(profileArgs: new() { ["-some_option"] = value })));
    }

    // ── Category: path and URL values ────────────────────────────────────────

    [Theory]
    [InlineData("/etc/example")]
    [InlineData("\\\\server\\share\\example")]
    [InlineData("C:\\Example\\file.txt")]
    [InlineData("C:/Example/file.txt")]
    [InlineData("~/example")]
    [InlineData("../example")]
    [InlineData("subdir/../../example")]
    public void PathValue_Fires(string value)
    {
        Assert.True(Fires(ProfileWith(profileArgs: new() { ["-some_option"] = value })));
    }

    [Theory]
    [InlineData("http://192.0.2.1/example")]
    [InlineData("https://example.test/example")]
    [InlineData("ftp://192.0.2.1/example")]
    [InlineData("tcp://192.0.2.1:9000")]
    [InlineData("udp://192.0.2.1:9000")]
    [InlineData("file:///tmp/example")]
    [InlineData("concat:a.ts|b.ts")]
    [InlineData("pipe:1")]
    [InlineData("data:text/plain;base64,AAAA")]
    public void UrlValue_Fires(string value)
    {
        Assert.True(Fires(ProfileWith(profileArgs: new() { ["-some_option"] = value })));
    }

    // ── All four argument sources are scanned ────────────────────────────────

    [Fact]
    public void DeniedFlag_FiresInProfileArguments()
    {
        Assert.True(
            Fires(ProfileWith(profileArgs: new() { ["-report"] = "1" }), "custom_arguments[")
        );
    }

    [Fact]
    public void DeniedFlag_FiresInVideoArguments()
    {
        Assert.True(
            Fires(
                ProfileWith(videoArgs: new() { ["-vstats_file"] = "x" }),
                "video.custom_arguments"
            )
        );
    }

    [Fact]
    public void DeniedFlag_FiresInAudioArguments()
    {
        Assert.True(
            Fires(ProfileWith(audioArgs: new() { ["-dump_attachment:t"] = "x" }), "audio[0]")
        );
    }

    [Fact]
    public void DeniedFlag_FiresInSubtitleArguments()
    {
        Assert.True(Fires(ProfileWith(subtitleArgs: new() { ["-attach"] = "x" }), "subtitles[0]"));
    }

    [Fact]
    public void UnsafeValue_FiresInEachSource()
    {
        Dictionary<string, string> bad = new() { ["-some_option"] = "movie=example.mkv" };

        Assert.True(Fires(ProfileWith(profileArgs: bad), "custom_arguments["));
        Assert.True(Fires(ProfileWith(videoArgs: bad), "video.custom_arguments"));
        Assert.True(Fires(ProfileWith(audioArgs: bad), "audio[0]"));
        Assert.True(Fires(ProfileWith(subtitleArgs: bad), "subtitles[0]"));
    }

    [Fact]
    public void Message_NamesTheKeyButNeverTheValue()
    {
        EncodingProfile profile = ProfileWith(
            profileArgs: new() { ["-attach"] = "/secret/example/path" }
        );

        EncoderRule rule = ProfileRuleValidator
            .Validate(profile)
            .Errors.First(r => r.Id == EncoderRuleId.CustomArgsUnsafeFlag);

        Assert.Contains("-attach", rule.Message);
        Assert.DoesNotContain("/secret/example/path", rule.Message + rule.Fix + rule.Field);
    }

    // ── Legitimate arguments keep passing ────────────────────────────────────

    [Theory]
    [InlineData("-x264opts", "no-scenecut")]
    [InlineData("-x264-params", "nal-hrd=cbr:aq-mode=3")]
    [InlineData("-x265-params", "profile=main10:rdoq-level=2")]
    [InlineData("-max_muxing_queue_size", "1024")]
    [InlineData("-aac_coder", "twoloop")]
    [InlineData("-canvas_size", "1920x1080")]
    [InlineData("-loglevel", "info")]
    [InlineData("-threads", "4")]
    [InlineData("-fps_mode", "cfr")]
    [InlineData("-metadata:s:a:0", "language=eng")]
    [InlineData("-ss", "00:01:30")]
    [InlineData("-an", "")]
    [InlineData("x264.psy-rd", "1.0,0.15")]
    [InlineData("-af", "loudnorm=I=-16:TP=-1.5")]
    public void LegitimateArgument_DoesNotFire(string key, string value)
    {
        Assert.False(Fires(ProfileWith(profileArgs: new() { [key] = value })));
    }

    [Fact]
    public void EveryBuiltinPreset_DoesNotFire()
    {
        foreach (EncodingProfile preset in BuiltinPresets.All())
            Assert.False(
                ProfileRuleValidator
                    .Validate(preset)
                    .Errors.Any(r => r.Id == EncoderRuleId.CustomArgsUnsafeFlag),
                $"preset '{preset.Name}' must not trip the unsafe-flag rule"
            );
    }

    [Fact]
    public void EveryBuiltinPresetArgument_PassesTheGuard()
    {
        int checkedCount = 0;
        foreach (EncodingProfile preset in BuiltinPresets.All())
        {
            List<Dictionary<string, string>?> sources =
            [
                preset.CustomArguments,
                preset.Video?.CustomArguments,
                .. (preset.Audio ?? []).Select(a => a.CustomArguments),
                .. (preset.Subtitles ?? []).Select(s => s.CustomArguments),
            ];

            foreach (Dictionary<string, string>? source in sources)
            foreach ((string key, string value) in source ?? [])
            {
                checkedCount++;
                Assert.False(
                    CustomArgumentGuard.TryGetViolation(key, value, true, out string reason),
                    $"preset '{preset.Name}' argument '{key}' tripped the guard: {reason}"
                );
            }
        }

        // The shipped presets use tunings without extras; the tuning table is the real source.
        foreach (VideoCodecType codec in Enum.GetValues<VideoCodecType>())
        foreach (EncodingQuality quality in Enum.GetValues<EncodingQuality>())
        {
            Dictionary<string, string>? extras;
            try
            {
                extras = CodecTunings.For(codec, quality).ExtraArgs;
            }
            catch (ArgumentOutOfRangeException)
            {
                continue;
            }

            foreach ((string key, string value) in extras ?? [])
            {
                checkedCount++;
                Assert.False(
                    CustomArgumentGuard.TryGetViolation(key, value, true, out string reason),
                    $"tuning {codec}/{quality} argument '{key}' tripped the guard: {reason}"
                );
            }
        }

        Assert.Equal(12, checkedCount);
    }
}
