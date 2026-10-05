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

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NoMercy.NmSystem.Information;
using NoMercy.NmSystem.Lifecycle;
using NoMercy.Storage.Drivers.Local;
using NoMercy.Storage.Validation;

namespace NoMercy.Encoder.Startup;

/// <summary>
/// Marks <see cref="BootStage.Binaries"/> complete the moment ffmpeg and
/// ffprobe are found on disk, independent of the whisper-model and
/// tesseract-language-data downloads that run after them inside
/// <c>Binaries.DownloadAll</c> and can take minutes. Every non-encoder queue
/// that needs ffprobe (library scans, file matching, music tagging,
/// chapter/color extraction) gates on this stage; without an independent
/// signal it waited on the entire binary bundle, including a multi-gigabyte
/// speech-recognition model it never touches.
/// <para>
/// <c>NoMercy.Setup.Boot.DegradedModeRecovery.TryProvisionBinariesAsync</c>
/// already treats "ffmpeg on disk" as sufficient to mark this stage on its
/// retry path; this service applies the same judgment on the ordinary boot
/// path, by polling rather than hooking into the download task itself.
/// </para>
/// </summary>
public sealed class FfmpegBinaryReadinessService(
    ILogger<FfmpegBinaryReadinessService> logger,
    IServerPhaseTracker? phaseTracker = null,
    int pollIntervalMs = 1_000
) : IHostedService
{
    private readonly CancellationTokenSource _stopping = new();

    internal Task PollTask { get; private set; } = Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (phaseTracker is null || phaseTracker.IsComplete(BootStage.Binaries))
            return Task.CompletedTask;

        CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _stopping.Token
        );

        PollTask = Task.Run(
            async () =>
            {
                try
                {
                    await PollUntilReadyAsync(linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
                finally
                {
                    linked.Dispose();
                }
            },
            linked.Token
        );

        return Task.CompletedTask;
    }

    private async Task PollUntilReadyAsync(CancellationToken ct)
    {
        LocalStorageDriver driver = new();
        LocalStorage storage = new(driver, new StoragePathGuard([], driver));

        while (!ct.IsCancellationRequested)
        {
            if (storage.Exists(AppFiles.FfmpegPath) && storage.Exists(AppFiles.FfProbePath))
            {
                phaseTracker?.MarkComplete(BootStage.Binaries);
                logger.LogInformation(
                    "ffmpeg and ffprobe found on disk — BootStage.Binaries marked complete "
                        + "(whisper models / tesseract language data may still be downloading)"
                );
                return;
            }

            await Task.Delay(pollIntervalMs, ct).ConfigureAwait(false);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping.Cancel();
        return Task.CompletedTask;
    }
}
