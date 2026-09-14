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
using NoMercy.Data.Services;
using NoMercy.Encoder.Codecs;
using NoMercy.Encoder.ContentAnalysis.Fingerprinting;
using Xunit;

namespace NoMercy.Tests.Repositories.Services;

/// <summary>
/// Covers the pure decision logic that used to be copy-pasted between
/// <c>EncoderContentAnalysisController</c> and its deprecated dashboard
/// alias: duration parsing, outro-window selection, target-format parsing,
/// and the fingerprint-hash digest. These no longer depend on ffmpeg, a
/// database, or a video file, so they run as plain unit tests.
/// </summary>
[Trait("Category", "Unit")]
public class ContentAnalysisServiceTests
{
    // ─── ParseDuration ────────────────────────────────────────────────────────

    [Fact]
    public void ParseDuration_ValidTimeSpanString_ReturnsParsedValue()
    {
        TimeSpan result = ContentAnalysisService.ParseDuration("00:42:10");

        result.Should().Be(new TimeSpan(0, 42, 10));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-duration")]
    public void ParseDuration_MissingOrMalformed_ReturnsZero(string? raw)
    {
        ContentAnalysisService.ParseDuration(raw).Should().Be(TimeSpan.Zero);
    }

    // ─── ResolveOutroStart ────────────────────────────────────────────────────

    [Fact]
    public void ResolveOutroStart_SourceLongerThanWindow_StartsWindowFromEnd()
    {
        TimeSpan duration = TimeSpan.FromMinutes(22);
        TimeSpan window = TimeSpan.FromMinutes(3);

        TimeSpan start = ContentAnalysisService.ResolveOutroStart(duration, window);

        start.Should().Be(TimeSpan.FromMinutes(19));
    }

    [Fact]
    public void ResolveOutroStart_SourceShorterThanWindow_StartsAtZero()
    {
        TimeSpan duration = TimeSpan.FromMinutes(2);
        TimeSpan window = TimeSpan.FromMinutes(3);

        TimeSpan start = ContentAnalysisService.ResolveOutroStart(duration, window);

        start.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void ResolveOutroStart_SourceExactlyWindowLength_StartsAtZero()
    {
        TimeSpan window = TimeSpan.FromMinutes(3);

        TimeSpan start = ContentAnalysisService.ResolveOutroStart(window, window);

        start.Should().Be(TimeSpan.Zero);
    }

    // ─── ParseTargetFormat ────────────────────────────────────────────────────

    [Theory]
    [InlineData("vtt", SubtitleCodecType.WebVtt)]
    [InlineData("webvtt", SubtitleCodecType.WebVtt)]
    [InlineData("WEBVTT", SubtitleCodecType.WebVtt)]
    [InlineData("ass", SubtitleCodecType.Ass)]
    [InlineData("srt", SubtitleCodecType.Srt)]
    [InlineData("unknown-format", SubtitleCodecType.WebVtt)]
    [InlineData(null, SubtitleCodecType.WebVtt)]
    public void ParseTargetFormat_ResolvesExpectedCodec(string? raw, SubtitleCodecType expected)
    {
        ContentAnalysisService.ParseTargetFormat(raw).Should().Be(expected);
    }

    // ─── ComputeFingerprintHash ───────────────────────────────────────────────

    [Fact]
    public void ComputeFingerprintHash_SamePrints_ProducesSameHash()
    {
        List<AudioFingerprint> prints =
        [
            new([1, 2, 3, 4], TimeSpan.FromMilliseconds(186), TimeSpan.Zero),
            new([5, 6, 7, 8], TimeSpan.FromMilliseconds(186), TimeSpan.Zero),
        ];

        string first = ContentAnalysisService.ComputeFingerprintHash(prints);
        string second = ContentAnalysisService.ComputeFingerprintHash(prints);

        first.Should().Be(second);
        first.Should().MatchRegex("^[0-9a-f]{32}$");
    }

    [Fact]
    public void ComputeFingerprintHash_DifferentPrints_ProducesDifferentHash()
    {
        List<AudioFingerprint> printsA =
        [
            new([1, 2, 3, 4], TimeSpan.FromMilliseconds(186), TimeSpan.Zero),
        ];
        List<AudioFingerprint> printsB =
        [
            new([9, 9, 9, 9], TimeSpan.FromMilliseconds(186), TimeSpan.Zero),
        ];

        string hashA = ContentAnalysisService.ComputeFingerprintHash(printsA);
        string hashB = ContentAnalysisService.ComputeFingerprintHash(printsB);

        hashA.Should().NotBe(hashB);
    }

    [Fact]
    public void ComputeFingerprintHash_MismatchedLengths_UsesShorterOverlap()
    {
        // Regression guard: XOR-reduction must not throw on ragged inputs —
        // fingerprints taken from episodes of slightly different length are
        // expected to have differing hash-array lengths.
        List<AudioFingerprint> prints =
        [
            new([1, 2, 3, 4, 5], TimeSpan.FromMilliseconds(186), TimeSpan.Zero),
            new([1, 2], TimeSpan.FromMilliseconds(186), TimeSpan.Zero),
        ];

        Action act = () => ContentAnalysisService.ComputeFingerprintHash(prints);

        act.Should().NotThrow();
    }
}
