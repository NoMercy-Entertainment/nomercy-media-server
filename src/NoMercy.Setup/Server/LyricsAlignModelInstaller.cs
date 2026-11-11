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

using NoMercy.Encoder.Startup;
using NoMercy.NmSystem.Extensions;
using NoMercy.NmSystem.SystemCalls;
using NoMercy.Storage;
using Serilog.Events;

namespace NoMercy.Setup.Server;

/// <summary>
/// The host side of <see cref="ILyricsAlignModelInstaller" />: one download
/// at a time, through the same verified path every other model takes.
/// <para>
/// A request that arrives while a download runs is dropped, and so is one
/// that arrives within <see cref="PauseBetweenAttempts" /> of the last one
/// ending, whether it succeeded or not. Without the pause a release that
/// carries no asset, or a provider that is down, would be asked again for
/// every track a plugin tries to align.
/// </para>
/// </summary>
public sealed class LyricsAlignModelInstaller : ILyricsAlignModelInstaller
{
    internal static readonly TimeSpan PauseBetweenAttempts = TimeSpan.FromMinutes(10);

    private readonly Func<Task> _download;
    private readonly TimeProvider _clock;
    private readonly Lock _gate = new();

    private Task? _running;
    private DateTimeOffset _notBefore = DateTimeOffset.MinValue;

    public LyricsAlignModelInstaller(IStorageDriver driver, IStorage storage)
        : this(() => new Binaries(driver, storage).DownloadLyricsAlignModel(), TimeProvider.System)
    { }

    // Test-only seam (NoMercy.Tests.Setup has InternalsVisibleTo): the
    // download itself is proven on Binaries, what is proven here is when one
    // is started.
    internal LyricsAlignModelInstaller(Func<Task> download, TimeProvider clock)
    {
        _download = download;
        _clock = clock;
    }

    /// <summary>The download that is running or last ran; null before the first request.</summary>
    internal Task? Running
    {
        get
        {
            lock (_gate)
                return _running;
        }
    }

    public void RequestInstall()
    {
        lock (_gate)
        {
            if (_running is { IsCompleted: false } || _clock.GetUtcNow() < _notBefore)
                return;

            _running = Task.Run(InstallAsync);
        }
    }

    private async Task InstallAsync()
    {
        try
        {
            Logger.Setup(
                "A plugin asked for the lyricsalign model; downloading it (about 600 MB)."
            );
            await _download();
        }
        catch (Exception exception)
        {
            Logger.Setup(
                $"Failed to download the lyricsalign model: {exception.Unwrap()}",
                LogEventLevel.Error
            );
        }
        finally
        {
            lock (_gate)
                _notBefore = _clock.GetUtcNow().Add(PauseBetweenAttempts);
        }
    }
}
