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
using NoMercy.Resources;
using NoMercy.Storage;
using NoMercy.Storage.Drivers.Local;

namespace NoMercy.Tests.Encoder.PostProcess;

/// <summary>
/// Which file the refresher samples frames from.
///
/// <para>Every title carrying a legacy <c>sprite.webp</c> predates the HLS
/// layout: one flat <c>.mp4</c> in the folder, no <c>video_*</c> rendition
/// folder anywhere. That is precisely the population the upgrade job exists to
/// serve, and looking only for rendition playlists made all 4218 queued jobs
/// give up on "nothing playable to sample".</para>
/// </summary>
public class SpriteSheetRefresherSourceTests : IDisposable
{
    private readonly string _root;
    private readonly string _mediaFolder;
    private readonly IStorage _storage;
    private readonly Mock<IMediaAnalyzer> _analyzer = new();
    private readonly Mock<IFfmpegExecutor> _executor = new();

    public SpriteSheetRefresherSourceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"nomercy-sprite-source-{Guid.NewGuid():N}");
        _mediaFolder = Path.Combine(_root, "Show.S01E01");
        Directory.CreateDirectory(_mediaFolder);

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

    [Fact]
    public async Task APreHlsTitle_IsSampledFromItsProgressiveFile()
    {
        WriteFile("Show.S01E01.NoMercy.mp4", 4_000);
        WriteFile("sprite.webp", 100);
        WriteFile("previews.vtt", 100);

        FfmpegCommand? command = await RunAsync(succeeds: false);

        command.Should().NotBeNull("the run reached ffmpeg instead of giving up");
        command!.Arguments.Should().ContainMatch("*Show.S01E01.NoMercy.mp4");
    }

    [Fact]
    public async Task AFolderHoldingAnExtra_IsSampledFromTheFeature()
    {
        WriteFile("Show.S01E01.NoMercy.mp4", 4_000);
        WriteFile("Behind.The.Scenes.mp4", 200);

        FfmpegCommand? command = await RunAsync(succeeds: false);

        command!.Arguments.Should().ContainMatch("*Show.S01E01.NoMercy.mp4");
    }

    [Fact]
    public async Task AnHlsTitle_StillPrefersItsRendition()
    {
        Directory.CreateDirectory(Path.Combine(_mediaFolder, "video_1920x1080"));
        File.WriteAllText(
            Path.Combine(_mediaFolder, "video_1920x1080", "video_1920x1080.m3u8"),
            "#EXTM3U"
        );
        WriteFile("Show.S01E01.NoMercy.mp4", 4_000);

        FfmpegCommand? command = await RunAsync(succeeds: false);

        command!.Arguments.Should().ContainMatch("*video_1920x1080.m3u8");
    }

    [Fact]
    public async Task ASuccessfulRebuild_TakesTheLegacyPairWithIt()
    {
        WriteFile("Show.S01E01.NoMercy.mp4", 4_000);
        WriteFile("sprite.webp", 100);
        WriteFile("previews.vtt", 100);

        await RunAsync(succeeds: true);

        File.Exists(Path.Combine(_mediaFolder, "sprite.webp")).Should().BeFalse();
        File.Exists(Path.Combine(_mediaFolder, "previews.vtt")).Should().BeFalse();
        File.Exists(Path.Combine(_mediaFolder, "Show.S01E01.NoMercy.mp4")).Should().BeTrue();
    }

    [Fact]
    public async Task TheThreadCap_SitsAheadOfTheInput_SoItReachesTheDecoder()
    {
        WriteFile("Show.S01E01.NoMercy.mp4", 4_000);

        FfmpegCommand? command = await RunAsync(succeeds: false);

        int threadsAt = Array.IndexOf(command!.Arguments, "-threads");
        int inputAt = Array.IndexOf(command.Arguments, "-i");

        threadsAt.Should().BeGreaterThan(-1);
        threadsAt
            .Should()
            .BeLessThan(
                inputAt,
                "an output -threads reaches the encoder only, and h264 decode then "
                    + "takes every core the box has"
            );
        command.Arguments[threadsAt + 1].Should().Be(EncodeThreadBudget.AuxiliaryPass.ToString());
    }

    private void WriteFile(string name, int bytes)
    {
        File.WriteAllBytes(Path.Combine(_mediaFolder, name), new byte[bytes]);
    }

    private async Task<FfmpegCommand?> RunAsync(bool succeeds)
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
            .ReturnsAsync(
                new ExecutionResult(succeeds, succeeds ? 0 : 1, string.Empty, TimeSpan.Zero, null)
            );

        SpriteSheetRefresher refresher = new(
            _analyzer.Object,
            _executor.Object,
            new() { FfmpegPathOverride = "ffmpeg" }
        );

        await refresher.RefreshAsync(_storage, _mediaFolder, 320, 10);

        return seen;
    }

    private static MediaInfo SampleMedia()
    {
        return new(
            FilePath: "source",
            Format: "mov,mp4",
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
}
