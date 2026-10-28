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

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using NoMercy.Database;
using NoMercy.Database.Maintenance;
using NoMercy.Database.Models.Music;

namespace NoMercy.MediaProcessing.Music;

/// <summary>
/// Marks Track rows whose file is gone as unavailable and removes them once the grace
/// period has passed. A row a user holds in a playlist or likes is marked but never
/// removed.
/// </summary>
public class MissingTrackPruner(MediaContext mediaContext, ILogger logger)
{
    public static readonly TimeSpan GracePeriod = TimeSpan.FromDays(30);

    private const int BatchSize = 500;

    public async Task<int> MarkMissingAsync(
        IReadOnlyCollection<Guid> trackIds,
        DateTime nowUtc,
        CancellationToken cancellationToken = default
    )
    {
        int marked = 0;
        foreach (Guid[] batch in trackIds.Chunk(BatchSize))
            marked += await mediaContext
                .Tracks.Where(track => batch.Contains(track.Id) && track.MissingSince == null)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(track => track.MissingSince, nowUtc),
                    cancellationToken
                );

        return marked;
    }

    public async Task<int> ClearMissingAsync(
        IReadOnlyCollection<Guid> trackIds,
        CancellationToken cancellationToken = default
    )
    {
        int cleared = 0;
        foreach (Guid[] batch in trackIds.Chunk(BatchSize))
            cleared += await mediaContext
                .Tracks.Where(track => batch.Contains(track.Id) && track.MissingSince != null)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(track => track.MissingSince, (DateTime?)null),
                    cancellationToken
                );

        return cleared;
    }

    /// <summary>Removes the rows a user does not hold; returns (removed, kept).</summary>
    public async Task<(int Removed, int Kept)> RemoveUnheldAsync(
        IReadOnlyCollection<Guid> trackIds,
        CancellationToken cancellationToken = default
    )
    {
        if (trackIds.Count == 0)
            return (0, 0);

        HashSet<Guid> heldByUsers = await UserHeldAsync(trackIds, cancellationToken);
        List<Guid> removable = [.. trackIds.Where(id => !heldByUsers.Contains(id))];

        if (removable.Count > 0)
            await RemoveAsync(removable, cancellationToken);

        logger.LogInformation(
            "Removed {Removed} track row(s) missing past the grace period; kept {Kept} that a playlist or like still holds",
            removable.Count,
            heldByUsers.Count
        );

        return (removable.Count, heldByUsers.Count);
    }

    private async Task<HashSet<Guid>> UserHeldAsync(
        IReadOnlyCollection<Guid> trackIds,
        CancellationToken cancellationToken
    )
    {
        HashSet<Guid> held = [];
        foreach (Guid[] batch in trackIds.Chunk(BatchSize))
        {
            held.UnionWith(
                await mediaContext
                    .PlaylistTrack.AsNoTracking()
                    .Where(link => batch.Contains(link.TrackId))
                    .Select(link => link.TrackId)
                    .ToListAsync(cancellationToken)
            );
            held.UnionWith(
                await mediaContext
                    .TrackUser.AsNoTracking()
                    .Where(link => batch.Contains(link.TrackId))
                    .Select(link => link.TrackId)
                    .ToListAsync(cancellationToken)
            );
        }

        return held;
    }

    private async Task RemoveAsync(List<Guid> trackIds, CancellationToken cancellationToken)
    {
        await using IDbContextTransaction transaction =
            await mediaContext.Database.BeginTransactionAsync(cancellationToken);

        await RestrictedChildren.ClearAsync(
            mediaContext,
            typeof(Track),
            trackIds,
            cancellationToken
        );

        foreach (Guid[] batch in trackIds.Chunk(BatchSize))
            await mediaContext
                .Tracks.Where(track => batch.Contains(track.Id))
                .ExecuteDeleteAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }
}
