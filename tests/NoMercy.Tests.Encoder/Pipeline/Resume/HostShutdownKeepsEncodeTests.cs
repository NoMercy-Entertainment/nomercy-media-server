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
using NoMercy.Encoder.Codecs;
using NoMercy.Encoder.Orchestration;
using NoMercy.Encoder.Pipeline;
using NoMercy.Encoder.Progress;
using NoMercy.Encoder.Strategies;
using NoMercy.Encoder.Strategies.Hls;
using NoMercy.Storage;
using NoMercy.Storage.Drivers.Local;
using NoMercy.Tests.Encoder.Storage;
using Container = NoMercy.Encoder.Profiles.Container;

namespace NoMercy.Tests.Encoder.Pipeline.Resume;

/// <summary>
/// A cancel that comes from the host stopping (restart, update) keeps the working
/// folder so the encode can resume; a user cancel still wipes it.
/// </summary>
public class HostShutdownKeepsEncodeTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly List<string> _tempDirs = [];

    public HostShutdownKeepsEncodeTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"HostStop_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);

        foreach (string tempDir in _tempDirs.Where(Directory.Exists))
            Directory.Delete(tempDir, recursive: true);

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Strategy_HostStopping_KeepsSegmentsAndMarker()
    {
        (string segment, string marker) = await RunStrategyCancelAsync(hostStopping: true);

        File.Exists(segment).Should().BeTrue("a host stop must keep the segments for the resume");
        File.Exists(marker).Should().BeTrue("a host stop must keep the resume marker");
    }

    [Fact]
    public async Task Strategy_UserCancel_WipesSegmentsAndMarker()
    {
        (string segment, string marker) = await RunStrategyCancelAsync(hostStopping: false);

        File.Exists(segment).Should().BeFalse("a user cancel wipes the partial output");
        File.Exists(marker).Should().BeFalse("a user cancel wipes the resume marker");
    }

    [Fact]
    public async Task Orchestrator_HostStopping_KeepsWorkingFolder()
    {
        (string tempDir, string segment) = await RunOrchestratorCancelAsync(hostStopping: true);

        File.Exists(segment).Should().BeTrue("the working folder stays for the resume");
        Directory.Exists(tempDir).Should().BeTrue();
    }

    [Fact]
    public async Task Orchestrator_UserCancel_DeletesWorkingFolder()
    {
        (string tempDir, string _) = await RunOrchestratorCancelAsync(hostStopping: false);

        Directory.Exists(tempDir).Should().BeFalse("a user cancel still removes the folder");
    }

    private async Task<(string Segment, string Marker)> RunStrategyCancelAsync(bool hostStopping)
    {
        string outputDir = Path.Combine(_tempRoot, "output");
        Directory.CreateDirectory(outputDir);
        string segment = Path.Combine(outputDir, "video_00000.ts");
        string marker = Path.Combine(outputDir, ".resume.0123abcd");
        await File.WriteAllTextAsync(segment, "segment");
        await File.WriteAllTextAsync(marker, "started");

        using CancellationTokenSource cts = new();
        Mock<IEncoder> encoder = new();
        encoder
            .Setup(e =>
                e.EncodeAsync(
                    It.IsAny<EncodingRequest>(),
                    It.IsAny<IProgressObserver?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns(
                (EncodingRequest _, IProgressObserver? _, CancellationToken ct) =>
                {
                    cts.Cancel();
                    ct.ThrowIfCancellationRequested();
                    throw new InvalidOperationException("unreachable");
                }
            );

        HlsSinglePassStrategy strategy = new(
            encoder.Object,
            NullLogger<HlsSinglePassStrategy>.Instance,
            TestStorageFactory.CreateLocal()
        );

        EncodingRequest request = BuildRequest(outputDir) with
        {
            HostStopping = hostStopping ? cts.Token : CancellationToken.None,
        };

        Func<Task> act = async () => await strategy.EncodeAsync(request, null, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();

        return (segment, marker);
    }

    private async Task<(string TempDir, string Segment)> RunOrchestratorCancelAsync(
        bool hostStopping
    )
    {
        Mock<IStorage> storage = new();
        storage
            .Setup(s => s.AcquireLocalPathAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string path, CancellationToken _) => new(path));
        storage.Setup(s => s.Driver).Returns(new LocalStorageDriver());

        using CancellationTokenSource cts = new();
        string? segment = null;
        Mock<IEncodingStrategy> strategy = new();
        strategy.Setup(s => s.Format).Returns(OutputFormat.Hls);
        strategy.Setup(s => s.EncodeMode).Returns(EncodeMode.SinglePass);
        strategy
            .Setup(s =>
                s.EncodeAsync(
                    It.IsAny<EncodingRequest>(),
                    It.IsAny<IProgressObserver?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns(
                (EncodingRequest staged, IProgressObserver? _, CancellationToken ct) =>
                {
                    segment = Path.Combine(staged.OutputDirectory, "video_00000.ts");
                    File.WriteAllText(segment, "segment");
                    cts.Cancel();
                    ct.ThrowIfCancellationRequested();
                    throw new InvalidOperationException("unreachable");
                }
            );

        Mock<IStrategyResolver> resolver = new();
        resolver
            .Setup(r => r.Resolve(OutputFormat.Hls, EncodeMode.SinglePass))
            .Returns(strategy.Object);

        EncodingOrchestrator orchestrator = new(
            resolver.Object,
            storage.Object,
            new Mock<IEncoder>().Object,
            NullLogger<EncodingOrchestrator>.Instance
        );

        EncodingRequest request = BuildRequest($"nm-host-stop-{Guid.NewGuid():N}") with
        {
            HostStopping = hostStopping ? cts.Token : CancellationToken.None,
        };
        string tempDir = EncoderTempDirectory.Resolve(request.OutputDirectory);

        _tempDirs.Add(tempDir);
        EncodingResult result = await orchestrator.EncodeAsync(request, ct: cts.Token);
        result.Success.Should().BeFalse();
        return (tempDir, segment!);
    }

    private static EncodingRequest BuildRequest(string outputDir) =>
        new(
            InputPath: "/media/source/movie.mkv",
            OutputDirectory: outputDir,
            Profile: new(
                Id: Ulid.NewUlid(),
                Name: "Test HLS",
                Container: Container.HlsTs,
                Video: null,
                Audio: [],
                Subtitles: []
            )
        );
}
