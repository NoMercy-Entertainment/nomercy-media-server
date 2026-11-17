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
using NoMercy.Database.Models.TvShows;
using NoMercy.Encoder.Codecs;
using NoMercy.Encoder.ContentAnalysis;
using NoMercy.Encoder.Progress;
using NoMercy.Encoder.Subtitles;
using NoMercy.MediaProcessing.Segments;
using NoMercy.Storage;
using NoMercyQueue.Core.Interfaces;

namespace NoMercy.Tests.Repositories.Services;

/// <summary>
/// Covers the logic that used to be copy-pasted between
/// <c>EncoderContentAnalysisController</c> and its deprecated dashboard
/// alias: target-format parsing, the whisper call and the on-demand season
/// analysis (which queues the #598 job instead of running a detector).
/// </summary>
[Trait("Category", "Unit")]
public class ContentAnalysisServiceTests
{
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

    // ─── QueueSegmentAnalysisForSeasonAsync ───────────────────────────────────

    [Fact]
    public async Task SeasonAnalysis_QueuesTheNewJob_AndAnswersWithTheStoredSegments()
    {
        Mock<IVideoFileRepository> videoFiles = new();
        videoFiles
            .Setup(r => r.GetEncodedEpisodesForSeasonAsync(10, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new() { Id = 2, EpisodeNumber = 2 },
                new() { Id = 1, EpisodeNumber = 1 },
            ]);
        Mock<IContentSegmentRepository> segments = new();
        segments
            .Setup(r => r.GetForEpisodeAsync(1))
            .ReturnsAsync([
                new ContentSegment
                {
                    EpisodeId = 1,
                    SegmentType = ContentSegmentType.Intro,
                    StartSeconds = 5.56,
                    EndSeconds = 82.72,
                    Source = "analysis",
                    Confidence = 0.9,
                },
            ]);
        segments
            .Setup(r => r.GetForEpisodeAsync(2))
            .ReturnsAsync([
                new ContentSegment
                {
                    EpisodeId = 2,
                    SegmentType = ContentSegmentType.Outro,
                    StartSeconds = -240,
                    EndSeconds = -150,
                    Source = "detector",
                },
                new ContentSegment
                {
                    EpisodeId = 2,
                    SegmentType = ContentSegmentType.Credits,
                    StartSeconds = 1281.64,
                    EndSeconds = 1361.08,
                    Source = "analysis",
                    Confidence = 0.9,
                },
            ]);
        Mock<IJobDispatcher> dispatcher = new();
        Mock<ISegmentAnalysisState> state = new();

        ContentAnalysisService.SeasonAnalysisRequest request = await Service(
                videoFiles,
                segments,
                dispatcher,
                state
            )
            .QueueSegmentAnalysisForSeasonAsync(10, CancellationToken.None);

        request.Queued.Should().BeTrue();
        request.EpisodesFound.Should().Be(2);
        request.Intro!.StartSeconds.Should().Be(5.56);
        request
            .Outro!.StartSeconds.Should()
            .Be(1281.64, "an old negative detector outro is not an answer");
        dispatcher.Verify(
            d =>
                d.Dispatch(
                    It.Is<IShouldQueue>(j =>
                        ((NoMercy.MediaProcessing.Jobs.SegmentJobs.SegmentAnalysisJob)j).SeasonId
                        == 10
                    ),
                    "encoder",
                    1
                ),
            Times.Once
        );
        state.Verify(
            s =>
                s.ResetFailuresAsync(
                    SegmentAnalysisTarget.Season,
                    10,
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Fact]
    public async Task SeasonAnalysis_WithoutEncodedEpisodes_QueuesNothing()
    {
        Mock<IVideoFileRepository> videoFiles = new();
        videoFiles
            .Setup(r => r.GetEncodedEpisodesForSeasonAsync(10, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        Mock<IJobDispatcher> dispatcher = new();

        ContentAnalysisService.SeasonAnalysisRequest request = await Service(
                videoFiles,
                new(),
                dispatcher,
                new()
            )
            .QueueSegmentAnalysisForSeasonAsync(10, CancellationToken.None);

        request.Queued.Should().BeFalse();
        dispatcher.Verify(
            d => d.Dispatch(It.IsAny<IShouldQueue>(), It.IsAny<string>(), It.IsAny<int>()),
            Times.Never
        );
    }

    private static ContentAnalysisService Service(
        Mock<IVideoFileRepository> videoFiles,
        Mock<IContentSegmentRepository> segments,
        Mock<IJobDispatcher> dispatcher,
        Mock<ISegmentAnalysisState> state
    ) =>
        new(
            Mock.Of<ICropDetector>(),
            dispatcher.Object,
            state.Object,
            ocrEngine: null,
            whisperTranscriber: null,
            videoFiles.Object,
            segments.Object,
            Mock.Of<IStorageDriver>()
        );

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
            Mock.Of<IJobDispatcher>(),
            Mock.Of<ISegmentAnalysisState>(),
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
