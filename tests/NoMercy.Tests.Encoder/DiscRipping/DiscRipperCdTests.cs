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
using NoMercy.NmSystem.Dto;
using NoMercy.OpticalMedia.Rip;
using NoMercy.OpticalMedia.Sources;
using NoMercy.Storage.Drivers.Local;

namespace NoMercy.Tests.Encoder.DiscRipping;

/// <summary>
/// Verifies the CD-specific rip path in <see cref="DiscRipper"/>.
/// CD-DA tracks must use <c>-f libcdio</c> + <c>-map 0:a:N</c> + <c>-c:a flac</c>.
/// The shared video path (<c>-map 0:v:0</c> + <c>-c copy</c> + <c>.mkv</c>) must
/// NOT appear in any CD rip invocation.
/// </summary>
[Trait("Category", "Unit")]
public class DiscRipperCdTests : IDisposable
{
    private readonly string _outputDir;
    private readonly Mock<IProcessRunner> _processRunner = new();
    private readonly EncoderOptions _options = new()
    {
        FfmpegPathOverride = "/usr/bin/ffmpeg",
        FfprobePathOverride = "/usr/bin/ffprobe",
    };
    private readonly List<string[]> _capturedArgs = [];

    public DiscRipperCdTests()
    {
        _outputDir = Path.Combine(Path.GetTempPath(), $"CdRip_{Guid.NewGuid():N}");

        _processRunner
            .Setup(runner =>
                runner.RunAsync(
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

    // ── Format flags ──────────────────────────────────────────────────────

    [Fact]
    public async Task RipAsync_Cd_UsesLibcdioInputFormat()
    {
        DiscRipper ripper = BuildRipper();
        RipRequest request = CdRequest("/dev/sr0", [1]);

        await ripper.RipAsync(request, _outputDir, CancellationToken.None);

        _capturedArgs.Should().HaveCount(1);
        string[] args = _capturedArgs[0];
        int fIdx = Array.IndexOf(args, "-f");
        fIdx.Should().BeGreaterThanOrEqualTo(0, "CD rip must pass -f");
        args[fIdx + 1].Should().Be("libcdio");
    }

    [Fact]
    public async Task RipAsync_Cd_EncodesFlac()
    {
        DiscRipper ripper = BuildRipper();
        RipRequest request = CdRequest("/dev/sr0", [1]);

        await ripper.RipAsync(request, _outputDir, CancellationToken.None);

        string[] args = _capturedArgs[0];
        int caIdx = Array.IndexOf(args, "-c:a");
        caIdx.Should().BeGreaterThanOrEqualTo(0, "CD rip must pass -c:a");
        args[caIdx + 1].Should().Be("flac");
    }

    [Fact]
    public async Task RipAsync_Cd_OutputPathEndsWithFlac()
    {
        DiscRipper ripper = BuildRipper();
        RipRequest request = CdRequest("/dev/sr0", [1]);

        DiscRipResult[] results = await ripper.RipAsync(
            request,
            _outputDir,
            CancellationToken.None
        );

        results.Should().HaveCount(1);
        results[0].OutputPath.Should().EndWith(".flac");
    }

    // ── -map 0:v:0 must NOT appear ────────────────────────────────────────

    [Fact]
    public async Task RipAsync_Cd_DoesNotMapVideoStream()
    {
        DiscRipper ripper = BuildRipper();
        RipRequest request = CdRequest("/dev/sr0", [1]);

        await ripper.RipAsync(request, _outputDir, CancellationToken.None);

        string[] args = _capturedArgs[0];
        args.Should().NotContain("0:v:0", "CD-DA has no video stream");
    }

    [Fact]
    public async Task RipAsync_Cd_DoesNotUseMkvContainer()
    {
        DiscRipper ripper = BuildRipper();
        RipRequest request = CdRequest("/dev/sr0", [3]);

        DiscRipResult[] results = await ripper.RipAsync(
            request,
            _outputDir,
            CancellationToken.None
        );

        results[0].OutputPath.Should().NotEndWith(".mkv");
    }

    // ── Per-track stream mapping ──────────────────────────────────────────

    [Fact]
    public async Task RipAsync_Cd_Track1_MapsToStream0()
    {
        DiscRipper ripper = BuildRipper();
        RipRequest request = CdRequest("/dev/sr0", [1]);

        await ripper.RipAsync(request, _outputDir, CancellationToken.None);

        string[] args = _capturedArgs[0];
        int mapIdx = Array.IndexOf(args, "-map");
        mapIdx.Should().BeGreaterThanOrEqualTo(0);
        args[mapIdx + 1].Should().Be("0:a:0", "track 1 = stream index 0");
    }

    [Fact]
    public async Task RipAsync_Cd_Track5_MapsToStream4()
    {
        DiscRipper ripper = BuildRipper();
        RipRequest request = CdRequest("/dev/sr0", [5]);

        await ripper.RipAsync(request, _outputDir, CancellationToken.None);

        string[] args = _capturedArgs[0];
        int mapIdx = Array.IndexOf(args, "-map");
        args[mapIdx + 1].Should().Be("0:a:4", "track 5 = stream index 4");
    }

    // ── One invocation per track ──────────────────────────────────────────

    [Fact]
    public async Task RipAsync_Cd_MultipleTracksFireSeparateInvocations()
    {
        DiscRipper ripper = BuildRipper();
        RipRequest request = CdRequest("/dev/sr0", [1, 2, 3]);

        DiscRipResult[] results = await ripper.RipAsync(
            request,
            _outputDir,
            CancellationToken.None
        );

        results.Should().HaveCount(3);
        _capturedArgs.Should().HaveCount(3, "one ffmpeg call per CD track");
    }

    [Fact]
    public async Task RipAsync_Cd_EachInvocationMapsCorrectStream()
    {
        DiscRipper ripper = BuildRipper();
        RipRequest request = CdRequest("/dev/sr0", [2, 4]);

        await ripper.RipAsync(request, _outputDir, CancellationToken.None);

        _capturedArgs.Should().HaveCount(2);
        int mapIdx0 = Array.IndexOf(_capturedArgs[0], "-map");
        int mapIdx1 = Array.IndexOf(_capturedArgs[1], "-map");
        _capturedArgs[0][mapIdx0 + 1].Should().Be("0:a:1", "track 2 = stream 1");
        _capturedArgs[1][mapIdx1 + 1].Should().Be("0:a:3", "track 4 = stream 3");
    }

    // ── Output file naming ────────────────────────────────────────────────

    [Fact]
    public async Task RipAsync_Cd_OutputFileNameContainsZeroPaddedTrackNumber()
    {
        DiscRipper ripper = BuildRipper();
        RipRequest request = CdRequest("/dev/sr0", [3]);

        DiscRipResult[] results = await ripper.RipAsync(
            request,
            _outputDir,
            CancellationToken.None
        );

        string fileName = Path.GetFileName(results[0].OutputPath);
        fileName.Should().StartWith("03", "track number is zero-padded to 2 digits");
    }

    // ── Failure propagation ───────────────────────────────────────────────

    [Fact]
    public async Task RipAsync_Cd_FfmpegFailure_ReturnsFailureResult()
    {
        _processRunner.Reset();
        _processRunner
            .Setup(runner =>
                runner.RunAsync(
                    It.IsAny<string>(),
                    It.IsAny<string[]>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(new ProcessResult(1, "", "I/O error", TimeSpan.FromSeconds(1)));

        DiscRipper ripper = BuildRipper();
        RipRequest request = CdRequest("/dev/sr0", [1]);

        DiscRipResult[] results = await ripper.RipAsync(
            request,
            _outputDir,
            CancellationToken.None
        );

        results[0].Success.Should().BeFalse();
        results[0].Error.Should().Contain("exited with code 1");
    }

    // ── Helpers ───────────────────────────────────────────────────────────

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

    private static RipRequest CdRequest(string drivePath, int[] trackIndices) =>
        new(
            DrivePath: drivePath,
            SelectedTitleIndices: trackIndices,
            MetadataId: null,
            Custom: null,
            LibraryId: Ulid.NewUlid(),
            FolderId: Ulid.NewUlid(),
            EncodingProfileId: null,
            AudioTracks: [],
            Subtitles: [],
            DiscType: OpticalDiscType.Cd
        );
}
