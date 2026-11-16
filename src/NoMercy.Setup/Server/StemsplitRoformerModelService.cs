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
/// Fetches the stemsplit filter's RoFormer models on a machine that has a GPU
/// that can hold them, once the hardware detection has run.
/// <para>
/// The four files are 3.4 GB, and RoFormer on a CPU takes most of an hour per
/// minute of music, so a machine without a GPU never gets them and keeps
/// splitting with Spleeter. Whether RoFormer then really runs is the filter's
/// own verdict, taken on the first split; this only decides the download.
/// Spleeter is fetched at setup on every machine, as it always was.
/// </para>
/// <para>
/// The GPU list is the one the encoder's hardware initialisation found, read
/// once it is done (<see cref="BootStage.Hardware" />). Asked any earlier, the
/// detection drops every GPU: it only keeps a GPU whose encoders ffmpeg
/// offers, and that list is filled by the same initialisation.
/// </para>
/// <para>
/// A GPU whose reported memory is known and under <see cref="SmallestVramMb" />
/// cannot hold the two models of <c>preset=vocal_full</c> (2.51 GB), and the
/// filter would refuse every run on it; an integrated GPU usually reports
/// that little. A GPU whose memory is not known (reported as 0) still gets
/// the models, and the filter decides.
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

    /// <summary>The least reported GPU memory the models are fetched for: 2.5 GB. A GTX 1060 3GB qualifies.</summary>
    internal const long SmallestVramMb = 2560;

    /// <summary>How long this host's GPU list is waited for once the hardware stage is marked.</summary>
    private static readonly TimeSpan HardwareListWait = TimeSpan.FromMinutes(5);

    private readonly IHardwareDetector _hardwareDetector;
    private readonly HardwareCapabilitiesHolder _hardware;
    private readonly IServerPhaseTracker? _phaseTracker;
    private readonly Func<Task> _download;
    private readonly CancellationTokenSource _stopping = new();

    public StemsplitRoformerModelService(
        IHardwareDetector hardwareDetector,
        HardwareCapabilitiesHolder hardware,
        IStorageDriver driver,
        IStorage storage,
        IServerPhaseTracker? phaseTracker = null
    )
        : this(
            hardwareDetector,
            hardware,
            () => new Binaries(driver, storage).DownloadStemsplitRoformerModels(),
            phaseTracker
        ) { }

    // Test seam (NoMercy.Tests.Setup has InternalsVisibleTo): the download
    // itself is proven on Binaries, what is proven here is when one starts.
    internal StemsplitRoformerModelService(
        IHardwareDetector hardwareDetector,
        HardwareCapabilitiesHolder hardware,
        Func<Task> download,
        IServerPhaseTracker? phaseTracker = null
    )
    {
        _hardwareDetector = hardwareDetector;
        _hardware = hardware;
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
    /// The GPUs the hardware initialisation found. The stage is shared by
    /// every host of the process and the list is not: after the HTTPS restart
    /// the stage can already be marked while this host's initialisation is
    /// still running, so the list is waited for a while. A host without the
    /// initialisation asks the detection itself.
    /// </summary>
    private async Task<IReadOnlyList<GpuDevice>> GpuListAsync(CancellationToken ct)
    {
        if (_phaseTracker is not null)
        {
            DateTime giveUp = DateTime.UtcNow + HardwareListWait;
            while (_hardware.Current is null && DateTime.UtcNow < giveUp)
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }

        return _hardware.Current?.Gpus ?? await _hardwareDetector.DetectGpusAsync(ct);
    }

    /// <summary>A GPU with enough memory, or one whose memory nobody reported.</summary>
    private static bool CanHoldTheModels(GpuDevice gpu) =>
        gpu.VramMb <= 0 || gpu.VramMb >= SmallestVramMb;

    /// <summary>
    /// Waits for the hardware detection, and downloads the models when it
    /// found a GPU that can hold them. Never throws: every failure is a line
    /// in the log.
    /// </summary>
    internal async Task ProvisionAsync(CancellationToken ct)
    {
        try
        {
            // Hardware comes after Binaries: ffmpeg's folder is there too.
            if (_phaseTracker is not null)
                await _phaseTracker.WhenReachedAsync(BootStage.Hardware, ct);

            IReadOnlyList<GpuDevice> gpus = await GpuListAsync(ct);

            if (gpus.Count == 0)
            {
                Logger.Setup(
                    "No GPU found: the RoFormer stem models are not downloaded, stems are split with Spleeter.",
                    LogEventLevel.Information
                );
                return;
            }

            if (!gpus.Any(CanHoldTheModels))
            {
                Logger.Setup(
                    $"No GPU with {SmallestVramMb} MB of memory or more ({string.Join(", ", gpus.Select(gpu => $"{gpu.Name}: {gpu.VramMb} MB"))}): the RoFormer stem models are not downloaded, stems are split with Spleeter.",
                    LogEventLevel.Information
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
