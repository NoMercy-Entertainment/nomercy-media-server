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
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using NoMercy.Data.Plugins;
using NoMercy.Encoder.Composition;
using NoMercy.Encoder.Hardware;
using NoMercy.NmSystem.Information;
using NoMercy.Storage.Drivers.Local;
using NoMercy.Storage.Validation;

namespace NoMercy.Tests.Repositories.Plugins;

/// <summary>
/// RoFormer or Spleeter on this machine: the host offers RoFormer wherever
/// its models are installed, the filter's refusal sends every later split to
/// Spleeter, and a new ffmpeg build or a changed GPU or driver asks again.
/// Real files in a folder of the test's own, so the verdict is proven to
/// outlive the instance that wrote it.
/// </summary>
public sealed class StemsplitSourceSelectorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"nm-stemsplit-verdict-{Guid.NewGuid():N}"
    );
    private readonly LocalStorage _storage;
    private readonly Mock<IHardwareDetector> _detector = new();
    private readonly FakeTimeProvider _clock = new();
    private readonly EncoderOptions _options;
    private List<GpuDevice> _gpus = [Gpu("NVIDIA GeForce GTX 1060 3GB", "31.0.15.3623")];

    public StemsplitSourceSelectorTests()
    {
        Directory.CreateDirectory(_root);
        LocalStorageDriver driver = new();
        _storage = new(driver, new StoragePathGuard([], driver));

        string ffmpeg = Path.Combine(_root, "ffmpeg.exe");
        File.WriteAllText(ffmpeg, "ffmpeg v1.0.47");
        File.SetLastWriteTimeUtc(ffmpeg, new DateTime(2026, 11, 14, 16, 25, 42, DateTimeKind.Utc));
        _options = new() { FfmpegPathOverride = ffmpeg };

        _detector
            .Setup(detector => detector.DetectGpusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _gpus);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException) { }
    }

    private static GpuDevice Gpu(string name, string driver) =>
        new(GpuVendor.Nvidia, name, 3072, 3, [], driver);

    private string VerdictPath => Path.Combine(_root, "config", "stemsplit_verdict.json");

    private StemsplitSourceSelector CreateSelector() =>
        new(_storage, _detector.Object, _options, NullLogger<StemsplitSourceSelector>.Instance)
        {
            VerdictPath = VerdictPath,
            ModelFolder = _root,
            Clock = _clock,
        };

    private void InstallModels(IEnumerable<string> models)
    {
        foreach (string model in models)
            File.WriteAllText(Path.Combine(_root, model + ".gguf"), model);
    }

    [Fact]
    public async Task WithoutTheModels_ItIsSpleeter()
    {
        StemsplitSource source = await CreateSelector().ChooseAsync(RoformerPreset.Full);

        source.Should().Be(StemsplitSource.Spleeter);
        source.Options.Should().Be("model=" + AppFiles.StemsplitModel + ".gguf");
    }

    [Fact]
    public async Task WithTheVocalsPairOnly_VocalFullIsRoformer_ButFullIsNot()
    {
        InstallModels(AppFiles.StemsplitRoformerVocalModels);
        StemsplitSourceSelector selector = CreateSelector();

        (await selector.ChooseAsync(RoformerPreset.VocalFull))
            .Options.Should()
            .Be("preset=vocal_full:cpu_fallback=0");
        (await selector.ChooseAsync(RoformerPreset.Full)).Should().Be(StemsplitSource.Spleeter);
    }

    [Fact]
    public async Task WithAllFourModels_AndNoVerdict_ItIsRoformer_WithoutAskingForTheGpus()
    {
        InstallModels(AppFiles.StemsplitRoformerModels);

        StemsplitSource source = await CreateSelector().ChooseAsync(RoformerPreset.Full);

        source.Engine.Should().Be(StemsplitEngine.Roformer);
        source.Options.Should().Be("preset=full:cpu_fallback=0");
        source.ModelId.Should().Be("roformer-full");
        _detector.Verify(
            detector => detector.DetectGpusAsync(It.IsAny<CancellationToken>()),
            Times.Never
        );
    }

    [Fact]
    public async Task ANoGpuRefusal_IsRemembered_ForEveryPreset_AcrossARestart()
    {
        InstallModels(AppFiles.StemsplitRoformerModels);
        await CreateSelector()
            .RememberRefusalAsync([RoformerPreset.Full], StemsplitLogFixtures.NoGpuRefusal);

        StemsplitSourceSelector afterRestart = CreateSelector();

        (await afterRestart.ChooseAsync(RoformerPreset.Full))
            .Should()
            .Be(StemsplitSource.Spleeter);
        (await afterRestart.ChooseAsync(RoformerPreset.VocalFull))
            .Should()
            .Be(StemsplitSource.Spleeter);
        File.ReadAllText(VerdictPath).Should().Contain("no GPU of this machine can run RoFormer");
    }

    /// <summary>
    /// A card that cannot hold four models may hold two: a memory refusal of
    /// <c>full</c> leaves <c>vocal_full</c> to be asked on its own.
    /// </summary>
    [Fact]
    public async Task AMemoryRefusalOfFull_LeavesVocalFullToBeAsked()
    {
        InstallModels(AppFiles.StemsplitRoformerModels);
        StemsplitSourceSelector selector = CreateSelector();

        await selector.RememberRefusalAsync(
            [RoformerPreset.Full],
            "[Parsed_stemsplit_0 @ 0000022d2d352340] stemsplit: cpu_fallback=0 and 2 of the run's 4 model(s) would run on the CPU; refused rather than run on the CPU."
        );

        (await selector.ChooseAsync(RoformerPreset.Full)).Should().Be(StemsplitSource.Spleeter);
        (await selector.ChooseAsync(RoformerPreset.VocalFull))
            .Engine.Should()
            .Be(StemsplitEngine.Roformer);
    }

    /// <summary>The four models hold the two, so a card that refused the two refuses the four.</summary>
    [Fact]
    public async Task AMemoryRefusalOfVocalFull_RefusesFullToo()
    {
        InstallModels(AppFiles.StemsplitRoformerModels);
        StemsplitSourceSelector selector = CreateSelector();

        await selector.RememberRefusalAsync(
            [RoformerPreset.VocalFull],
            StemsplitLogFixtures.PartialMemoryRefusal
        );

        (await selector.ChooseAsync(RoformerPreset.VocalFull))
            .Should()
            .Be(StemsplitSource.Spleeter);
        (await selector.ChooseAsync(RoformerPreset.Full)).Should().Be(StemsplitSource.Spleeter);
    }

    [Fact]
    public async Task AGraphWithBothPresets_RefusedForTwoModels_RefusesTheVocalPreset()
    {
        InstallModels(AppFiles.StemsplitRoformerModels);
        StemsplitSourceSelector selector = CreateSelector();

        await selector.RememberRefusalAsync(
            [RoformerPreset.Full, RoformerPreset.VocalFull],
            StemsplitLogFixtures.PartialMemoryRefusal
        );

        File.ReadAllText(VerdictPath).Should().Contain("\"vocal_full\"");
        (await selector.ChooseAsync(RoformerPreset.VocalFull))
            .Should()
            .Be(StemsplitSource.Spleeter);
    }

    [Fact]
    public async Task ANewFfmpegBuild_AsksTheFilterAgain_AndDropsTheVerdict()
    {
        InstallModels(AppFiles.StemsplitRoformerModels);
        await CreateSelector()
            .RememberRefusalAsync([RoformerPreset.Full], StemsplitLogFixtures.NoGpuRefusal);

        File.SetLastWriteTimeUtc(
            _options.FfmpegPathOverride!,
            new DateTime(2026, 11, 20, 9, 0, 0, DateTimeKind.Utc)
        );

        (await CreateSelector().ChooseAsync(RoformerPreset.Full))
            .Engine.Should()
            .Be(StemsplitEngine.Roformer);
        File.Exists(VerdictPath).Should().BeFalse();
    }

    [Fact]
    public async Task ANewGpuDriver_AsksTheFilterAgain()
    {
        InstallModels(AppFiles.StemsplitRoformerModels);
        await CreateSelector()
            .RememberRefusalAsync([RoformerPreset.Full], StemsplitLogFixtures.NoGpuRefusal);

        _gpus = [Gpu("NVIDIA GeForce GTX 1060 3GB", "32.0.15.6094")];

        (await CreateSelector().ChooseAsync(RoformerPreset.Full))
            .Engine.Should()
            .Be(StemsplitEngine.Roformer);
    }

    [Fact]
    public async Task ANewGpu_AsksTheFilterAgain()
    {
        InstallModels(AppFiles.StemsplitRoformerModels);
        await CreateSelector()
            .RememberRefusalAsync([RoformerPreset.Full], StemsplitLogFixtures.NoGpuRefusal);

        _gpus = [.. _gpus, Gpu("NVIDIA GeForce RTX 3070", "32.0.15.6094")];

        (await CreateSelector().ChooseAsync(RoformerPreset.Full))
            .Engine.Should()
            .Be(StemsplitEngine.Roformer);
    }

    /// <summary>A sweep asks once per track; the GPU list is read once and kept for a while.</summary>
    [Fact]
    public async Task TheGpuList_IsReadOnce_WithinItsLifetime()
    {
        InstallModels(AppFiles.StemsplitRoformerModels);
        StemsplitSourceSelector selector = CreateSelector();
        await selector.RememberRefusalAsync(
            [RoformerPreset.Full],
            StemsplitLogFixtures.NoGpuRefusal
        );

        for (int track = 0; track < 5; track++)
            await selector.ChooseAsync(RoformerPreset.Full);

        _detector.Verify(
            detector => detector.DetectGpusAsync(It.IsAny<CancellationToken>()),
            Times.Once
        );

        _clock.Advance(StemsplitSourceSelector.GpuListLifetime);
        await selector.ChooseAsync(RoformerPreset.Full);

        _detector.Verify(
            detector => detector.DetectGpusAsync(It.IsAny<CancellationToken>()),
            Times.Exactly(2)
        );
    }

    [Fact]
    public async Task AnUnreadableVerdict_IsNoVerdict()
    {
        InstallModels(AppFiles.StemsplitRoformerModels);
        Directory.CreateDirectory(Path.GetDirectoryName(VerdictPath)!);
        File.WriteAllText(VerdictPath, "{ not json");

        (await CreateSelector().ChooseAsync(RoformerPreset.Full))
            .Engine.Should()
            .Be(StemsplitEngine.Roformer);
    }
}
