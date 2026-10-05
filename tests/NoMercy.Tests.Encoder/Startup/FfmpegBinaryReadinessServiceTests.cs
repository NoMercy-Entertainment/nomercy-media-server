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
using NoMercy.Encoder.Startup;
using NoMercy.NmSystem.Information;
using NoMercy.NmSystem.Lifecycle;

namespace NoMercy.Tests.Encoder.Startup;

/// <summary>
/// Exercises the real polling loop against the real filesystem at
/// <see cref="AppFiles.FfmpegPath"/> / <see cref="AppFiles.FfProbePath"/> — the
/// same paths <c>Binaries.DownloadFfmpeg</c> writes to — so this proves the
/// exact regression scenario: <see cref="BootStage.Binaries"/> becomes
/// complete the moment ffmpeg and ffprobe land on disk, independent of the
/// whisper-model / tesseract downloads that follow them in
/// <c>Binaries.DownloadAll</c> and were never created here.
/// </summary>
[Trait("Category", "Unit")]
public sealed class FfmpegBinaryReadinessServiceTests : IDisposable
{
    private readonly string _ffmpegBackup;
    private readonly string _ffprobeBackup;
    private readonly bool _ffmpegExisted;
    private readonly bool _ffprobeExisted;

    public FfmpegBinaryReadinessServiceTests()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(AppFiles.FfmpegPath)!);

        _ffmpegBackup = AppFiles.FfmpegPath + ".readiness-test-bak";
        _ffprobeBackup = AppFiles.FfProbePath + ".readiness-test-bak";

        _ffmpegExisted = File.Exists(AppFiles.FfmpegPath);
        if (_ffmpegExisted)
            File.Move(AppFiles.FfmpegPath, _ffmpegBackup);

        _ffprobeExisted = File.Exists(AppFiles.FfProbePath);
        if (_ffprobeExisted)
            File.Move(AppFiles.FfProbePath, _ffprobeBackup);
    }

    public void Dispose()
    {
        if (File.Exists(AppFiles.FfmpegPath))
            File.Delete(AppFiles.FfmpegPath);
        if (File.Exists(AppFiles.FfProbePath))
            File.Delete(AppFiles.FfProbePath);

        if (_ffmpegExisted)
            File.Move(_ffmpegBackup, AppFiles.FfmpegPath);
        if (_ffprobeExisted)
            File.Move(_ffprobeBackup, AppFiles.FfProbePath);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, string failureMessage)
    {
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        while (!cts.IsCancellationRequested)
        {
            if (predicate())
                return;
            await Task.Delay(20);
        }
        throw new TimeoutException(failureMessage);
    }

    [Fact]
    public async Task MarksBinariesComplete_AssoonAsBothFfmpegAndFfprobeExist()
    {
        ServerPhaseTracker tracker = new();
        FfmpegBinaryReadinessService service = new(
            NullLogger<FfmpegBinaryReadinessService>.Instance,
            tracker,
            pollIntervalMs: 20
        );

        await service.StartAsync(CancellationToken.None);

        await Task.Delay(100);
        tracker.IsComplete(BootStage.Binaries).Should().BeFalse();

        await File.WriteAllTextAsync(AppFiles.FfmpegPath, "stub");

        await Task.Delay(100);
        tracker
            .IsComplete(BootStage.Binaries)
            .Should()
            .BeFalse("ffprobe has not landed yet — ffmpeg alone is not sufficient");

        await File.WriteAllTextAsync(AppFiles.FfProbePath, "stub");

        await WaitUntilAsync(
            () => tracker.IsComplete(BootStage.Binaries),
            "BootStage.Binaries must be marked complete once both ffmpeg and ffprobe exist"
        );

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task NeverMarksBinariesComplete_WhenNeitherBinaryIsEverWritten()
    {
        ServerPhaseTracker tracker = new();
        FfmpegBinaryReadinessService service = new(
            NullLogger<FfmpegBinaryReadinessService>.Instance,
            tracker,
            pollIntervalMs: 20
        );

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(200);

        tracker
            .IsComplete(BootStage.Binaries)
            .Should()
            .BeFalse("this models a library scan that must not start before ffmpeg is on disk");

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AlreadyComplete_DoesNotStartPolling()
    {
        ServerPhaseTracker tracker = new();
        tracker.MarkComplete(BootStage.Binaries);

        FfmpegBinaryReadinessService service = new(
            NullLogger<FfmpegBinaryReadinessService>.Instance,
            tracker,
            pollIntervalMs: 20
        );

        await service.StartAsync(CancellationToken.None);

        service.PollTask.IsCompleted.Should().BeTrue();

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StopAsync_EndsThePollLoop()
    {
        ServerPhaseTracker tracker = new();
        FfmpegBinaryReadinessService service = new(
            NullLogger<FfmpegBinaryReadinessService>.Instance,
            tracker,
            pollIntervalMs: 20
        );

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(50);

        await service.StopAsync(CancellationToken.None);

        Task finished = await Task.WhenAny(service.PollTask, Task.Delay(2000));
        finished.Should().BeSameAs(service.PollTask, "StopAsync must end the poll loop promptly");
    }
}
