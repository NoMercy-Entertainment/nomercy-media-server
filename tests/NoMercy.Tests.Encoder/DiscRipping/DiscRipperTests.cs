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

using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NoMercy.Encoder.Composition;
using NoMercy.Encoder.Infrastructure;
using NoMercy.Encoder.Profiles;
using NoMercy.OpticalMedia.Rip;
using NoMercy.OpticalMedia.Sources;
using NoMercy.Storage.Drivers.Local;

namespace NoMercy.Tests.Encoder.DiscRipping;

/// <summary>
/// Verifies the FFmpeg command <see cref="DiscRipper"/> builds for each
/// disc type and track-selection combination. The ripper shells out to
/// FFmpeg via <see cref="IProcessRunner"/>; we intercept that call to
/// inspect the argv without running a real process.
/// </summary>
public class DiscRipperTests : IDisposable
{
    private readonly string _outputDir;
    private readonly Mock<IProcessRunner> _processRunner = new();
    private readonly EncoderOptions _options = new()
    {
        FfmpegPathOverride = "/usr/bin/ffmpeg",
        FfprobePathOverride = "/usr/bin/ffprobe",
    };
    private readonly List<string[]> _capturedArgs = [];

    public DiscRipperTests()
    {
        _outputDir = Path.Combine(Path.GetTempPath(), $"Rip_{Guid.NewGuid():N}");

        _processRunner
            .Setup(r =>
                r.RunAsync(
                    It.IsAny<string>(),
                    It.IsAny<string[]>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Callback<string, string[], string?, CancellationToken>(
                (_, args, _, _) => _capturedArgs.Add(args)
            )
            .ReturnsAsync(new ProcessResult(0, "", "", TimeSpan.FromSeconds(1)));
    }

    public void Dispose()
    {
        if (Directory.Exists(_outputDir))
            Directory.Delete(_outputDir, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task RipAsync_Bluray_UsesPlaylistFlagForTitle()
    {
        DiscRipper ripper = BuildRipper();
        RipRequest request = Request(
            drivePath: "bluray:/dev/sr0",
            titles: [0],
            audioTracks: [new(0, true)],
            subtitles: []
        );

        DiscRipResult[] results = await ripper.RipAsync(
            request,
            _outputDir,
            CancellationToken.None
        );

        results.Should().HaveCount(1);
        results[0].Success.Should().BeTrue();
        _capturedArgs.Should().HaveCount(1);
        _capturedArgs[0].Should().Contain("-playlist").And.Contain("0");
        _capturedArgs[0].Should().Contain("bluray:/dev/sr0");
    }

    [Fact]
    public async Task RipAsync_Dvd_DoesNotAddPlaylistFlag()
    {
        DiscRipper ripper = BuildRipper();
        RipRequest request = Request(
            drivePath: "D:\\",
            titles: [0],
            audioTracks: [new(0, true)],
            subtitles: []
        );

        await ripper.RipAsync(request, _outputDir, CancellationToken.None);

        _capturedArgs[0].Should().NotContain("-playlist");
        _capturedArgs[0].Should().Contain("D:\\");
    }

    [Fact]
    public async Task RipAsync_OnlyIncludesSelectedAudioTracks()
    {
        DiscRipper ripper = BuildRipper();
        RipRequest request = Request(
            drivePath: "bluray:/dev/sr0",
            titles: [0],
            audioTracks: [new(0, true), new(1, false), new(2, true)],
            subtitles: []
        );

        await ripper.RipAsync(request, _outputDir, CancellationToken.None);

        string[] args = _capturedArgs[0];
        // Two -map entries for audio (stream 0 and 2) + one for video.
        args.Where(a => a == "-map").Should().HaveCount(3);
        args.Should().Contain("0:a:0").And.Contain("0:a:2");
        args.Should().NotContain("0:a:1");
    }

    [Fact]
    public async Task RipAsync_IncludesOnlySelectedSubtitles()
    {
        DiscRipper ripper = BuildRipper();
        RipRequest request = Request(
            drivePath: "bluray:/dev/sr0",
            titles: [0],
            audioTracks: [new(0, true)],
            subtitles: [new(0, true, SubtitlePolicy.Copy), new(1, false, SubtitlePolicy.Copy)]
        );

        await ripper.RipAsync(request, _outputDir, CancellationToken.None);

        string[] args = _capturedArgs[0];
        args.Should().Contain("0:s:0");
        args.Should().NotContain("0:s:1");
    }

    [Fact]
    public async Task RipAsync_StreamCopyPreservesQuality()
    {
        DiscRipper ripper = BuildRipper();
        RipRequest request = Request(
            drivePath: "D:\\",
            titles: [0],
            audioTracks: [new(0, true)],
            subtitles: []
        );

        await ripper.RipAsync(request, _outputDir, CancellationToken.None);

        string[] args = _capturedArgs[0];
        int cIdx = Array.IndexOf(args, "-c");
        cIdx.Should().BeGreaterThanOrEqualTo(0);
        args[cIdx + 1].Should().Be("copy");
    }

    [Fact]
    public async Task RipAsync_WritesTitleNumberedOutputPath()
    {
        DiscRipper ripper = BuildRipper();
        RipRequest request = Request(
            drivePath: "bluray:/dev/sr0",
            titles: [3, 7],
            audioTracks: [new(0, true)],
            subtitles: []
        );

        DiscRipResult[] results = await ripper.RipAsync(
            request,
            _outputDir,
            CancellationToken.None
        );

        results.Should().HaveCount(2);
        results[0].OutputPath.Should().EndWith("title_03.mkv");
        results[1].OutputPath.Should().EndWith("title_07.mkv");
    }

    [Fact]
    public async Task RipAsync_FfmpegFailure_ReturnsFailureResult()
    {
        _processRunner.Reset();
        _processRunner
            .Setup(r =>
                r.RunAsync(
                    It.IsAny<string>(),
                    It.IsAny<string[]>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(new ProcessResult(1, "", "disc read error", TimeSpan.FromSeconds(1)));

        DiscRipper ripper = BuildRipper();
        RipRequest request = Request(
            drivePath: "bluray:/dev/sr0",
            titles: [0],
            audioTracks: [new(0, true)],
            subtitles: []
        );

        DiscRipResult[] results = await ripper.RipAsync(
            request,
            _outputDir,
            CancellationToken.None
        );

        results[0].Success.Should().BeFalse();
        results[0].Error.Should().Contain("exited with code 1");
    }

    [Fact]
    public async Task RipAsync_CancellationToken_StopsLoopBetweenTitles()
    {
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        DiscRipper ripper = BuildRipper();
        RipRequest request = Request(
            drivePath: "bluray:/dev/sr0",
            titles: [0, 1, 2],
            audioTracks: [new(0, true)],
            subtitles: []
        );

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            ripper.RipAsync(request, _outputDir, cts.Token)
        );

        _capturedArgs.Should().BeEmpty();
    }

    private DiscRipper BuildRipper()
    {
        LocalStorageDriver driver = new();
        LocalStorage storage = new(driver, new([], driver));
        return new(
            _options,
            _processRunner.Object,
            storage,
            new(),
            NullLogger<DiscRipper>.Instance
        );
    }

    private static RipRequest Request(
        string drivePath,
        int[] titles,
        AudioTrackSelection[] audioTracks,
        SubtitleSelection[] subtitles
    ) =>
        new(
            DrivePath: drivePath,
            SelectedTitleIndices: titles,
            MetadataId: null,
            Custom: null,
            LibraryId: Ulid.NewUlid(),
            FolderId: Ulid.NewUlid(),
            EncodingProfileId: null,
            AudioTracks: audioTracks,
            Subtitles: subtitles
        );
}
