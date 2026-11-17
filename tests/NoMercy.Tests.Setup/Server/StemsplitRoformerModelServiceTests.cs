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
/// The RoFormer stem models are 3.4 GB and only usable on a GPU that can hold
/// them, so what is proven here is the one decision this service takes:
/// download them when the hardware detection, once it has run, reports such
/// a GPU, and only then. The download itself is proven on <see cref="Binaries" />.
/// </summary>
[Trait("Category", "Unit")]
public sealed class StemsplitRoformerModelServiceTests
{
    private readonly Mock<IHardwareDetector> _detector = new();
    private readonly HardwareCapabilitiesHolder _hardware = new();
    private int _downloads;

    private static GpuDevice Gpu(string name, long vramMb = 3072) =>
        new(GpuVendor.Nvidia, name, vramMb, 3, []);

    private void HardwareFound(params GpuDevice[] gpus) =>
        _hardware.Current = new HardwareCapabilities(gpus, 8);

    private StemsplitRoformerModelService CreateService(
        Func<Task>? download = null,
        IServerPhaseTracker? phaseTracker = null
    ) =>
        new(
            _detector.Object,
            _hardware,
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
        HardwareFound(Gpu("NVIDIA GeForce GTX 1060 3GB"));

        await CreateService().ProvisionAsync(CancellationToken.None);

        _downloads.Should().Be(1);
    }

    [Fact]
    public async Task AMachineWithoutAGpu_DownloadsNothing()
    {
        HardwareFound();

        await CreateService().ProvisionAsync(CancellationToken.None);

        _downloads.Should().Be(0);
    }

    /// <summary>
    /// An integrated GPU reports a few hundred MB to a GB or two; the two
    /// vocals models alone need 2.51 GB, so the filter would refuse every run.
    /// </summary>
    [Theory]
    [InlineData(128)]
    [InlineData(1024)]
    [InlineData(2048)]
    public async Task AGpuThatReportsTooLittleMemory_DownloadsNothing(long vramMb)
    {
        HardwareFound(Gpu("Intel(R) UHD Graphics 630", vramMb));

        await CreateService().ProvisionAsync(CancellationToken.None);

        _downloads.Should().Be(0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2560)]
    [InlineData(8192)]
    public async Task AGpuWithEnoughOrUnknownMemory_DownloadsTheModels(long vramMb)
    {
        HardwareFound(Gpu("A GPU", vramMb));

        await CreateService().ProvisionAsync(CancellationToken.None);

        _downloads.Should().Be(1);
    }

    [Fact]
    public async Task AnIntegratedGpuBesideADedicatedOne_DownloadsTheModels()
    {
        HardwareFound(Gpu("Intel(R) UHD Graphics 630", 1024), Gpu("NVIDIA GeForce RTX 3070", 8192));

        await CreateService().ProvisionAsync(CancellationToken.None);

        _downloads.Should().Be(1);
    }

    [Fact]
    public async Task AFailedDownload_IsLogged_NotThrown()
    {
        HardwareFound(Gpu("NVIDIA GeForce RTX 3070"));

        StemsplitRoformerModelService service = CreateService(() =>
            throw new AggregateException(new InvalidDataException("SHA-256 mismatch"))
        );

        Func<Task> provision = () => service.ProvisionAsync(CancellationToken.None);

        await provision.Should().NotThrowAsync();
    }

    /// <summary>
    /// Asked straight after the binaries, the detection drops every GPU: it
    /// keeps only a GPU whose encoders ffmpeg offers, and that list is filled
    /// by the hardware initialisation, which runs after the binaries too. So
    /// the service waits for that initialisation and reads the list it found.
    /// </summary>
    [Fact]
    public async Task TheGpuList_IsTheOneTheHardwareInitialisationFound_OnceItIsDone()
    {
        TaskCompletionSource hardwareDone = new();
        Mock<IServerPhaseTracker> tracker = new();
        tracker
            .Setup(phases =>
                phases.WhenReachedAsync(BootStage.Hardware, It.IsAny<CancellationToken>())
            )
            .Returns(hardwareDone.Task);

        // What the detection says before the encoder list is filled.
        _detector
            .Setup(detector => detector.DetectGpusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        Task provision = CreateService(phaseTracker: tracker.Object)
            .ProvisionAsync(CancellationToken.None);
        await Task.Yield();
        _downloads.Should().Be(0);

        HardwareFound(Gpu("NVIDIA GeForce RTX 3070", 8192));
        hardwareDone.SetResult();
        await provision;

        _downloads.Should().Be(1);
        _detector.Verify(
            detector => detector.DetectGpusAsync(It.IsAny<CancellationToken>()),
            Times.Never
        );
    }

    [Fact]
    public async Task WithoutAHardwareInitialisation_TheDetectionIsAsked()
    {
        _detector
            .Setup(detector => detector.DetectGpusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([Gpu("NVIDIA GeForce RTX 3070", 8192)]);

        await CreateService().ProvisionAsync(CancellationToken.None);

        _downloads.Should().Be(1);
    }
}
