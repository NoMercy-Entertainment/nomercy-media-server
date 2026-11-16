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
using NoMercy.Encoder.Hardware;
using NoMercy.NmSystem.Extensions;
using NoMercy.NmSystem.Lifecycle;
using NoMercy.NmSystem.SystemCalls;
using NoMercy.Storage;
using Serilog.Events;

namespace NoMercy.Setup.Server;

/// <summary>
/// Fetches the stemsplit filter's RoFormer models on a machine that has a GPU,
/// once ffmpeg is in place.
/// <para>
/// The four files are 3.4 GB, and RoFormer on a CPU takes most of an hour per
/// minute of music, so a machine without a GPU never gets them and keeps
/// splitting with Spleeter. Whether RoFormer then really runs is the filter's
/// own verdict, taken on the first split; this only decides the download.
/// Spleeter is fetched at setup on every machine, as it always was.
/// </para>
/// <para>
/// Runs in the background: a download of this size must not hold up the
/// start of the server, and a failure is logged and tried again at the next
/// start rather than stopping anything.
/// </para>
/// </summary>
public sealed class StemsplitRoformerModelService : IHostedService, IDisposable
{
    // One provisioning at a time in the whole process, not per instance: the
    // Service rebuilds its host on the HTTPS restart, and the first host's
    // download keeps running after that host stopped (Binaries takes no
    // token). A second host's copy waits for it, then finds the files current.
    private static readonly SemaphoreSlim OneDownloadPerProcess = new(1, 1);

    private readonly IHardwareDetector _hardwareDetector;
    private readonly IServerPhaseTracker? _phaseTracker;
    private readonly Func<Task> _download;
    private readonly CancellationTokenSource _stopping = new();

    public StemsplitRoformerModelService(
        IHardwareDetector hardwareDetector,
        IStorageDriver driver,
        IStorage storage,
        IServerPhaseTracker? phaseTracker = null
    )
        : this(
            hardwareDetector,
            () => new Binaries(driver, storage).DownloadStemsplitRoformerModels(),
            phaseTracker
        ) { }

    // Test seam (NoMercy.Tests.Setup has InternalsVisibleTo): the download
    // itself is proven on Binaries, what is proven here is when one starts.
    internal StemsplitRoformerModelService(
        IHardwareDetector hardwareDetector,
        Func<Task> download,
        IServerPhaseTracker? phaseTracker = null
    )
    {
        _hardwareDetector = hardwareDetector;
        _download = download;
        _phaseTracker = phaseTracker;
    }

    /// <summary>The background run, so a test can await it.</summary>
    internal Task Running { get; private set; } = Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Running = Task.Run(() => ProvisionAsync(_stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync();
    }

    public void Dispose() => _stopping.Dispose();

    /// <summary>
    /// Waits for ffmpeg's folder, asks the hardware detection for a GPU, and
    /// downloads the models when there is one. Never throws: every failure is
    /// a line in the log.
    /// </summary>
    internal async Task ProvisionAsync(CancellationToken ct)
    {
        try
        {
            if (_phaseTracker is not null)
                await _phaseTracker.WhenReachedAsync(BootStage.Binaries, ct);

            IReadOnlyList<GpuDevice> gpus = await _hardwareDetector.DetectGpusAsync(ct);
            if (gpus.Count == 0)
            {
                Logger.Setup(
                    "No GPU found: the RoFormer stem models are not downloaded, stems are split with Spleeter.",
                    LogEventLevel.Debug
                );
                return;
            }

            await OneDownloadPerProcess.WaitAsync(ct);
            try
            {
                await _download();
            }
            finally
            {
                OneDownloadPerProcess.Release();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The server is stopping; the next start tries again.
        }
        catch (Exception exception)
        {
            Logger.Setup(
                $"Failed to download the RoFormer stem models: {exception.Unwrap()}",
                LogEventLevel.Error
            );
        }
    }
}
