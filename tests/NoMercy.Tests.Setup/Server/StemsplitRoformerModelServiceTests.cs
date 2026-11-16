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
using NoMercy.Encoder.Hardware;
using NoMercy.NmSystem.Lifecycle;
using NoMercy.Setup.Server;

namespace NoMercy.Tests.Setup.Server;

/// <summary>
/// The RoFormer stem models are 3.4 GB and only usable on a GPU, so what is
/// proven here is the one decision this service takes: download them when the
/// hardware detection reports a GPU, and only then. The download itself is
/// proven on <see cref="Binaries" />.
/// </summary>
[Trait("Category", "Unit")]
public sealed class StemsplitRoformerModelServiceTests
{
    private readonly Mock<IHardwareDetector> _detector = new();
    private int _downloads;

    private static GpuDevice Gpu(string name) => new(GpuVendor.Nvidia, name, 3072, 3, []);

    private StemsplitRoformerModelService CreateService(
        Func<Task>? download = null,
        IServerPhaseTracker? phaseTracker = null
    ) =>
        new(
            _detector.Object,
            download
                ?? (
                    () =>
                    {
                        Interlocked.Increment(ref _downloads);
                        return Task.CompletedTask;
                    }
                ),
            phaseTracker
        );

    [Fact]
    public async Task AMachineWithAGpu_DownloadsTheModels()
    {
        _detector
            .Setup(detector => detector.DetectGpusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([Gpu("NVIDIA GeForce GTX 1060 3GB")]);

        await CreateService().ProvisionAsync(CancellationToken.None);

        _downloads.Should().Be(1);
    }

    [Fact]
    public async Task AMachineWithoutAGpu_DownloadsNothing()
    {
        _detector
            .Setup(detector => detector.DetectGpusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        await CreateService().ProvisionAsync(CancellationToken.None);

        _downloads.Should().Be(0);
    }

    [Fact]
    public async Task AFailedDownload_IsLogged_NotThrown()
    {
        _detector
            .Setup(detector => detector.DetectGpusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([Gpu("NVIDIA GeForce RTX 3070")]);

        StemsplitRoformerModelService service = CreateService(
            () => throw new AggregateException(new InvalidDataException("SHA-256 mismatch"))
        );

        Func<Task> provision = () => service.ProvisionAsync(CancellationToken.None);

        await provision.Should().NotThrowAsync();
    }

    /// <summary>
    /// Asked before ffmpeg's folder exists, the download would race the ffmpeg
    /// install that creates it; the service waits for that stage first.
    /// </summary>
    [Fact]
    public async Task TheGpuIsOnlyAskedFor_OnceTheBinariesStageIsReached()
    {
        TaskCompletionSource binaries = new();
        Mock<IServerPhaseTracker> tracker = new();
        tracker
            .Setup(phases =>
                phases.WhenReachedAsync(BootStage.Binaries, It.IsAny<CancellationToken>())
            )
            .Returns(binaries.Task);
        _detector
            .Setup(detector => detector.DetectGpusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([Gpu("NVIDIA GeForce RTX 3070")]);

        Task provision = CreateService(phaseTracker: tracker.Object)
            .ProvisionAsync(CancellationToken.None);
        await Task.Yield();

        _detector.Verify(
            detector => detector.DetectGpusAsync(It.IsAny<CancellationToken>()),
            Times.Never
        );

        binaries.SetResult();
        await provision;

        _downloads.Should().Be(1);
    }
}
