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

using Moq;
using NoMercy.Encoder.Analysis;
using NoMercy.Encoder.Commands;
using NoMercy.Encoder.Execution;
using NoMercy.Encoder.PostProcess;
using NoMercy.Encoder.Progress;
using NoMercy.Storage;
using NoMercy.Storage.Drivers.Local;

namespace NoMercy.Tests.Encoder.PostProcess;

/// <summary>
/// Sampling a full-length title is minutes of ffmpeg. Without progress the
/// dashboard had two observable states for that whole stretch — queued, and
/// gone — and a card that sits still for four minutes is indistinguishable from
/// a queue that has stopped moving.
///
/// <para>The pipe follows the caller: ffmpeg is only asked for progress lines
/// when somebody is there to receive them.</para>
/// </summary>
public class SpriteSheetRefresherProgressTests : IDisposable
{
    private readonly string _root;
    private readonly string _mediaFolder;
    private readonly IStorage _storage;
    private readonly Mock<IMediaAnalyzer> _analyzer = new();
    private readonly Mock<IFfmpegExecutor> _executor = new();

    public SpriteSheetRefresherProgressTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"nomercy-sprite-progress-{Guid.NewGuid():N}");
        _mediaFolder = Path.Combine(_root, "Show.S01E01");

        Directory.CreateDirectory(Path.Combine(_mediaFolder, "video_1920x1080"));
        File.WriteAllText(
            Path.Combine(_mediaFolder, "video_1920x1080", "video_1920x1080.m3u8"),
            "#EXTM3U"
        );

        _storage = new LocalStorage(new LocalStorageDriver(), new([], new LocalStorageDriver()));

        _analyzer
            .Setup(analyzer =>
                analyzer.AnalyzeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())
            )
            .ReturnsAsync(SampleMedia());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
        GC.SuppressFinalize(this);
    }

    private static MediaInfo SampleMedia()
    {
        return new(
            FilePath: "video_1920x1080.m3u8",
            Format: "hls",
            Duration: TimeSpan.FromMinutes(24),
            OverallBitRateKbps: 4000,
            FileSizeBytes: 700_000_000,
            VideoStreams:
            [
                new(
                    Index: 0,
                    Codec: "h264",
                    Width: 1920,
                    Height: 1080,
                    FrameRate: 23.976,
                    BitDepth: 8,
                    PixelFormat: "yuv420p",
                    ColorPrimaries: "bt709",
                    ColorTransfer: "bt709",
                    ColorSpace: "bt709",
                    IsDefault: true,
                    BitRateKbps: 4000
                ),
            ],
            AudioStreams: [],
            SubtitleStreams: [],
            Chapters: []
        );
    }

    private SpriteSheetRefresher Refresher()
    {
        return new(_analyzer.Object, _executor.Object, new() { FfmpegPathOverride = "ffmpeg" });
    }

    private async Task<FfmpegCommand?> RunAndCaptureCommandAsync(
        Action<EncodingProgress>? onProgress
    )
    {
        FfmpegCommand? seen = null;

        _executor
            .Setup(executor =>
                executor.ExecuteAsync(
                    It.IsAny<FfmpegCommand>(),
                    It.IsAny<TimeSpan>(),
                    It.IsAny<Action<EncodingProgress>?>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Callback(
                (
                    FfmpegCommand command,
                    TimeSpan _,
                    Action<EncodingProgress>? _,
                    string? _,
                    CancellationToken _
                ) => seen = command
            )
            .ReturnsAsync(new ExecutionResult(false, 1, string.Empty, TimeSpan.Zero, null));

        await Refresher().RefreshAsync(_storage, _mediaFolder, 320, 10, onProgress);

        return seen;
    }

    [Fact]
    public async Task AListeningCaller_GetsFfmpegsProgressPipe()
    {
        FfmpegCommand? command = await RunAndCaptureCommandAsync(_ => { });

        command.Should().NotBeNull("the run reached ffmpeg");
        command!.Arguments.Should().Contain("-progress");
    }

    [Fact]
    public async Task NoListener_LeavesThePipeOff()
    {
        FfmpegCommand? command = await RunAndCaptureCommandAsync(null);

        command.Should().NotBeNull("the run reached ffmpeg");
        command!.Arguments.Should().NotContain("-progress");
    }

    [Fact]
    public async Task EveryTickTheExecutorReports_ReachesTheCaller()
    {
        List<double> seen = [];

        _executor
            .Setup(executor =>
                executor.ExecuteAsync(
                    It.IsAny<FfmpegCommand>(),
                    It.IsAny<TimeSpan>(),
                    It.IsAny<Action<EncodingProgress>?>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Callback(
                (
                    FfmpegCommand _,
                    TimeSpan _,
                    Action<EncodingProgress>? onProgress,
                    string? _,
                    CancellationToken _
                ) =>
                {
                    onProgress!(Tick(12.5));
                    onProgress(Tick(80));
                }
            )
            .ReturnsAsync(new ExecutionResult(false, 1, string.Empty, TimeSpan.Zero, null));

        await Refresher()
            .RefreshAsync(
                _storage,
                _mediaFolder,
                320,
                10,
                progress => seen.Add(progress.PercentComplete)
            );

        seen.Should().Equal(12.5, 80);
    }

    private static EncodingProgress Tick(double percent)
    {
        return new(
            CorrelationId: "sprite",
            PercentComplete: percent,
            Elapsed: TimeSpan.FromSeconds(1),
            EstimatedRemaining: null,
            CurrentFps: null,
            CurrentSpeed: null,
            CurrentStage: null,
            CurrentOperation: null
        );
    }
}
