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

using System.Diagnostics;
using NoMercy.Data.Repositories;

namespace NoMercy.Service.Workers;

/// <summary>
/// On boot, runs each of MusicHub.StartPlaybackCommand's GetPlaylist query shapes once
/// against a Guid.Empty probe (matches zero rows, touches no real data) so EF Core's
/// one-time-per-process model building and query-plan compilation happen here instead of
/// on a real user's first tap. Measured live: the FIRST call of any of these shapes in a
/// fresh process costs low seconds even once the query itself is fast (see the
/// GetAlbumTracksAsync fix in the same commit) — every subsequent call is sub-second. Runs
/// fire-and-forget like <see cref="PaletteBackfillStartupService"/>: it must never delay
/// "Server started" or block Kestrel from accepting connections, and a failure here is not
/// fatal — the first real playback start simply pays the cold-query cost instead.
/// </summary>
public class MusicQueryWarmupService(
    IServiceScopeFactory scopeFactory,
    ILogger<MusicQueryWarmupService> logger
) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = WarmupAsync(cancellationToken);
        return Task.CompletedTask;
    }

    // internal (not private) so NoMercy.Tests.Api can await the work directly instead of
    // racing the fire-and-forget dispatch in StartAsync — see InternalsVisibleTo above.
    internal async Task WarmupAsync(CancellationToken cancellationToken)
    {
        try
        {
            Stopwatch stopwatch = Stopwatch.StartNew();

            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
            IMusicRepository repository =
                scope.ServiceProvider.GetRequiredService<IMusicRepository>();

            Guid probe = Guid.Empty;
            await repository.GetPlaylistTracksAsync(probe, probe, cancellationToken);
            await repository.GetAlbumTracksAsync(probe, probe, cancellationToken);
            await repository.GetArtistTracksAsync(probe, probe, cancellationToken);
            await repository.GetGenreTracksAsync(probe, probe, cancellationToken);
            await repository.GetTrackAsync(probe, cancellationToken);

            stopwatch.Stop();
            logger.LogInformation(
                "Music query warmup completed in {ElapsedMilliseconds}ms",
                stopwatch.ElapsedMilliseconds
            );
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Music query warmup failed; continuing — the first real playback start will pay the cold-query cost instead"
            );
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
