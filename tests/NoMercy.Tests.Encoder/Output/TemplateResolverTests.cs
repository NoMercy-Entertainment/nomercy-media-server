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
/// TemplateResolver drives the output file paths. Every variant / audio /
/// subtitle file gets a name derived from the profile's name template,
/// with :token: substitutions. If a token isn't replaced (typo, missing
/// value, case mismatch) the colon survives into the filesystem name —
/// broken on Windows, ugly on Linux. These tests pin the substitution
/// surface.
/// </summary>
public class TemplateResolverTests
{
    [Fact]
    public void Resolve_SingleToken_Replaced()
    {
        string result = TemplateResolver.Resolve(":type:_video.ts", new() { ["type"] = "video" });
        result.Should().Be("video_video.ts");
    }

    [Fact]
    public void Resolve_MultipleTokens_AllReplaced()
    {
        string result = TemplateResolver.Resolve(
            ":type:_:framesize:_:colorrange:/:type:_:framesize:_:colorrange:",
            new()
            {
                ["type"] = "video",
                ["framesize"] = "1920x1080",
                ["colorrange"] = "SDR",
            }
        );
        result.Should().Be("video_1920x1080_SDR/video_1920x1080_SDR");
    }

    [Fact]
    public void Resolve_UnknownToken_LeftIntact()
    {
        // Leaving unreplaced tokens in the output is the safest behavior:
        // the string is obviously broken on disk, which is a much louder
        // failure than silently dropping the token and producing two files
        // with the same name.
        string result = TemplateResolver.Resolve(":type:_:unknown:", new() { ["type"] = "video" });
        result.Should().Be("video_:unknown:");
    }

    [Fact]
    public void Resolve_EmptyTokens_ReturnsTemplate()
    {
        string result = TemplateResolver.Resolve(":type:_video", []);
        result.Should().Be(":type:_video");
    }

    [Fact]
    public void VideoTokens_IncludesTypeAndFrameSize()
    {
        Dictionary<string, string> tokens = TemplateResolver.VideoTokens(
            1920,
            1080,
            isHdrOutput: false
        );
        tokens["type"].Should().Be("video");
        tokens["framesize"].Should().Be("1920x1080");
        tokens["colorrange"].Should().Be("SDR");
    }

    [Fact]
    public void VideoTokens_IsHdrOutput_LabelsAsHdr()
    {
        // The colorrange token derives from the actual HDR pipeline (PQ / HLG /
        // SMPTE2084 transfer preserved end-to-end), NOT from bit depth. A
        // 10-bit BT.709 anime master is SDR and must be labelled SDR; an
        // HDR-passthrough output gets HDR regardless of any other field.
        Dictionary<string, string> tokens = TemplateResolver.VideoTokens(
            3840,
            2160,
            isHdrOutput: true
        );
        tokens["colorrange"].Should().Be("HDR");
    }

    [Fact]
    public void VideoTokens_TenBitButSdrPipeline_LabelsAsSdr()
    {
        // Regression for the "anime 10-bit folder named _HDR" bug. 10-bit
        // colour depth alone never makes an output HDR — only the transfer
        // pipeline does. Caller passes isHdrOutput = false for these.
        Dictionary<string, string> tokens = TemplateResolver.VideoTokens(
            1920,
            1080,
            isHdrOutput: false
        );
        tokens["colorrange"].Should().Be("SDR");
    }

    [Fact]
    public void AudioTokens_IncludesLanguageCodecChannels()
    {
        Dictionary<string, string> tokens = TemplateResolver.AudioTokens(
            language: "eng",
            codecName: "eac3",
            channels: 6
        );
        tokens["type"].Should().Be("audio");
        tokens["language"].Should().Be("eng");
        tokens["codec"].Should().Be("eac3");
        tokens["channels"].Should().Be("6");
    }

    // AUD-1174: a track tag never leaves the output folder.
    [Theory]
    [InlineData("../../x", "x")]
    [InlineData("..\\..\\x", "x")]
    [InlineData("/x", "x")]
    [InlineData("a.b", "ab")]
    [InlineData("..", "und")]
    [InlineData("", "und")]
    [InlineData("eng", "eng")]
    [InlineData("pt-BR", "pt-BR")]
    [InlineData("zh_Hans", "zh_Hans")]
    public void AudioTokens_LanguageWithPathSegments_IsReducedToASafeSegment(
        string language,
        string expected
    )
    {
        Dictionary<string, string> tokens = TemplateResolver.AudioTokens(language, "aac", 2);

        tokens["language"].Should().Be(expected);
        tokens["lang"].Should().Be(expected);
        TemplateResolver
            .Resolve("audio_{lang}_{codec}/audio_{lang}_{codec}", tokens)
            .Should()
            .Be($"audio_{expected}_aac/audio_{expected}_aac");
    }

    [Theory]
    [InlineData("../../x", "x")]
    [InlineData("..\\..\\x", "x")]
    [InlineData("/x", "x")]
    [InlineData("a.b", "ab")]
    [InlineData("..", "und")]
    [InlineData("", "und")]
    [InlineData("eng", "eng")]
    [InlineData("pt-BR", "pt-BR")]
    public void SubtitleTokens_LanguageWithPathSegments_IsReducedToASafeSegment(
        string language,
        string expected
    )
    {
        Dictionary<string, string> tokens = TemplateResolver.SubtitleTokens(
            language,
            "full",
            "movie"
        );

        tokens["language"].Should().Be(expected);
        tokens["lang"].Should().Be(expected);
    }

    [Fact]
    public void SubtitleTokens_IncludesVariantAndFilename()
    {
        Dictionary<string, string> tokens = TemplateResolver.SubtitleTokens(
            language: "eng",
            variant: "sign",
            filename: "movie"
        );
        tokens["language"].Should().Be("eng");
        tokens["variant"].Should().Be("sign");
        tokens["filename"].Should().Be("movie");
    }

    [Fact]
    public void Resolve_VideoTemplate_EndToEnd()
    {
        // The default video template from EncodingProfile:
        string template = ":type:_:framesize:_:colorrange:/:type:_:framesize:_:colorrange:";
        Dictionary<string, string> tokens = TemplateResolver.VideoTokens(
            1280,
            720,
            isHdrOutput: false
        );

        string resolved = TemplateResolver.Resolve(template, tokens);

        resolved.Should().Be("video_1280x720_SDR/video_1280x720_SDR");
    }

    [Fact]
    public void Resolve_AudioTemplate_EndToEnd()
    {
        string template = ":type:_:language:_:codec:/:type:_:language:_:codec:";
        Dictionary<string, string> tokens = TemplateResolver.AudioTokens("jpn", "opus", 2);

        string resolved = TemplateResolver.Resolve(template, tokens);

        resolved.Should().Be("audio_jpn_opus/audio_jpn_opus");
    }

    // ── Brace syntax (V2 built-in preset form) ──────────────────────────────

    [Fact]
    public void Resolve_BraceSyntax_SingleToken_Replaced()
    {
        // V2 built-in presets use {token} not :token:
        string result = TemplateResolver.Resolve(
            "video/{label}",
            new() { ["label"] = "1920x1080" }
        );
        result.Should().Be("video/1920x1080");
    }

    [Fact]
    public void Resolve_BraceSyntax_MultipleTokens_AllReplaced()
    {
        string result = TemplateResolver.Resolve(
            "{type}_{label}/{type}_{label}",
            new() { ["type"] = "video", ["label"] = "1280x720_SDR" }
        );
        result.Should().Be("video_1280x720_SDR/video_1280x720_SDR");
    }

    [Fact]
    public void Resolve_BraceSyntax_AudioPreset_EndToEnd()
    {
        // Verbatim V2 brace-style audio preset template.
        string template = "audio/{lang}-{codec}";
        Dictionary<string, string> tokens = TemplateResolver.AudioTokens("eng", "aac", 2);

        string resolved = TemplateResolver.Resolve(template, tokens);

        resolved.Should().Be("audio/eng-aac");
    }

    [Fact]
    public void Resolve_MixedColonAndBrace_BothReplaced()
    {
        // Legacy + modern in the same template — both must resolve so we can
        // ship V2 presets while V1 rows still live in the DB.
        string result = TemplateResolver.Resolve(
            ":type:_{label}",
            new() { ["type"] = "video", ["label"] = "1080p" }
        );
        result.Should().Be("video_1080p");
    }

    // ── HDR label suffix logic ──────────────────────────────────────────────

    [Fact]
    public void VideoTokens_SdrOutput_LabelHasSdrSuffix()
    {
        // SDR variants append "_SDR" to disambiguate from HDR variants of
        // the same resolution that get the bare framesize.
        Dictionary<string, string> tokens = TemplateResolver.VideoTokens(
            1920,
            795,
            isHdrOutput: false
        );
        tokens["label"].Should().Be("1920x795_SDR");
    }

    [Fact]
    public void VideoTokens_HdrOutput_LabelIsBareFrameSize()
    {
        // HDR keeps the bare framesize — matches the example media layout
        // where "1920x795/" is the HDR folder and "1920x795_SDR/" is the
        // tonemapped sibling.
        Dictionary<string, string> tokens = TemplateResolver.VideoTokens(
            1920,
            795,
            isHdrOutput: true
        );
        tokens["label"].Should().Be("1920x795");
    }

    [Fact]
    public void VideoTokens_ColorspaceAndColorrange_AreAliases()
    {
        // Both tokens carry the same value — templates can use either form.
        Dictionary<string, string> sdr = TemplateResolver.VideoTokens(1920, 1080, false);
        sdr["colorspace"].Should().Be(sdr["colorrange"]);

        Dictionary<string, string> hdr = TemplateResolver.VideoTokens(3840, 2160, true);
        hdr["colorspace"].Should().Be("HDR");
        hdr["colorspace"].Should().Be(hdr["colorrange"]);
    }

    // ── Audio aliases ───────────────────────────────────────────────────────

    [Fact]
    public void AudioTokens_LanguageAndLang_AreAliases()
    {
        // {lang} is the V2 brace-style alias for :language: — templates
        // written in either form must resolve identically.
        Dictionary<string, string> tokens = TemplateResolver.AudioTokens("jpn", "opus", 2);
        tokens["language"].Should().Be("jpn");
        tokens["lang"].Should().Be("jpn");
    }

    // ── Subtitle tokens / variant-as-type alias ────────────────────────────

    [Fact]
    public void SubtitleTokens_LangAlias_PresentAndMatchesLanguage()
    {
        Dictionary<string, string> tokens = TemplateResolver.SubtitleTokens(
            language: "fra",
            variant: "full",
            filename: "movie"
        );
        tokens["lang"].Should().Be("fra");
        tokens["language"].Should().Be("fra");
    }

    [Fact]
    public void SubtitleTokens_TypeAliasesVariant()
    {
        // In subtitle context {type} is the variant — different from
        // video/audio where {type} is the literal "video"/"audio". This
        // dual meaning is by design so the standard subtitle template
        // "subtitles/{filename}.{lang}.{type}" works without per-variant
        // surgery.
        Dictionary<string, string> tokens = TemplateResolver.SubtitleTokens(
            language: "eng",
            variant: "sign",
            filename: "movie"
        );
        tokens["type"].Should().Be("sign");
        tokens["variant"].Should().Be("sign");
    }

    [Fact]
    public void Resolve_SubtitleTemplate_EndToEnd()
    {
        Dictionary<string, string> tokens = TemplateResolver.SubtitleTokens(
            language: "eng",
            variant: "sdh",
            filename: "Movie.2024"
        );
        string template = "subtitles/{filename}.{lang}.{type}.vtt";

        string resolved = TemplateResolver.Resolve(template, tokens);

        resolved.Should().Be("subtitles/Movie.2024.eng.sdh.vtt");
    }
}
