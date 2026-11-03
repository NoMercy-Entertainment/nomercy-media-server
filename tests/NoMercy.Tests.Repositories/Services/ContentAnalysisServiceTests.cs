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
using Moq;
using NoMercy.Data.Repositories;
using NoMercy.Data.Services;
using NoMercy.Database.Models.Media;
using NoMercy.Encoder.Codecs;
using NoMercy.Encoder.ContentAnalysis;
using NoMercy.Encoder.ContentAnalysis.Fingerprinting;
using NoMercy.Encoder.Progress;
using NoMercy.Encoder.Subtitles;
using NoMercy.Storage;

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

    // ─── TranscribeAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task TranscribeAsync_SendsNoModelPathOverride()
    {
        // The service has no model opinion. An empty-string override used to
        // hide the configured EncoderOptions.WhisperModelPath (issue #239).
        VideoFile file = new() { HostFolder = "/media/show", Filename = "ep1.mkv" };
        Mock<IVideoFileRepository> videoFiles = new();
        videoFiles
            .Setup(r => r.GetByIdAsync(file.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(file);

        Mock<IStorageDriver> storageDriver = new();
        storageDriver
            .Setup(d => d.CombinePath(It.IsAny<string>(), It.IsAny<string[]>()))
            .Returns("/media/show/ep1.mkv");
        storageDriver.Setup(d => d.FileExists("/media/show/ep1.mkv")).Returns(true);

        WhisperOptions? sent = null;
        Mock<IWhisperTranscriber> transcriber = new();
        transcriber
            .Setup(t =>
                t.TranscribeAsync(
                    It.IsAny<string>(),
                    It.IsAny<int>(),
                    It.IsAny<string>(),
                    It.IsAny<WhisperOptions?>(),
                    It.IsAny<IProgressObserver?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Callback<string, int, string, WhisperOptions?, IProgressObserver?, CancellationToken>(
                (_, _, _, options, _, _) => sent = options
            )
            .ReturnsAsync(
                new SubtitleTrack(
                    "/media/show/ep1.eng.whisper.srt",
                    "eng",
                    SubtitleCodecType.Srt,
                    0
                )
            );

        ContentAnalysisService service = new(
            Mock.Of<ICropDetector>(),
            Mock.Of<IAudioFingerprinter>(),
            Mock.Of<IIntroDetector>(),
            ocrEngine: null,
            transcriber.Object,
            videoFiles.Object,
            Mock.Of<IContentSegmentRepository>(),
            storageDriver.Object
        );

        ContentAnalysisService.WhisperOutcome outcome = await service.TranscribeAsync(
            file.Id,
            audioStreamIndex: 0,
            language: "eng",
            modelSize: WhisperModelSize.Small,
            translateToEnglish: false,
            progress: null,
            ct: CancellationToken.None
        );

        outcome.Track.Should().NotBeNull();
        sent.Should().NotBeNull();
        sent!
            .ModelPath.Should()
            .BeNull("the service must not override the configured whisper model");
        sent.ModelSize.Should().Be(WhisperModelSize.Small);
    }
}
