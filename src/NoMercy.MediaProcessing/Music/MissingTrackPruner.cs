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
using NoMercy.Storage;

namespace NoMercy.MediaProcessing.Music;

/// <summary>
/// Drops the Track rows of one library folder whose file is gone, so a rescan no longer
/// lists tracks that play nothing. A row a user holds in a playlist or likes is kept and
/// only counted, and nothing is touched while the folder root is unreadable (an unmounted
/// share), because then every file looks gone.
/// </summary>
public class MissingTrackPruner(MediaContext mediaContext, ILogger logger)
{
    private const int BatchSize = 500;

    private readonly record struct TrackLocation(Guid Id, string HostFolder, string Filename);

    public async Task<int> PruneAsync(
        Ulid folderId,
        string scanRoot,
        IStorageDriver driver,
        CancellationToken cancellationToken = default
    )
    {
        if (!driver.DirectoryExists(scanRoot))
        {
            logger.LogWarning(
                "Not pruning missing tracks: library folder root '{Root}' is not readable",
                scanRoot
            );
            return 0;
        }

        List<TrackLocation> locations = await mediaContext
            .Tracks.AsNoTracking()
            .Where(track =>
                track.FolderId == folderId
                && track.HostFolder != null
                && track.HostFolder != ""
                && track.Filename != null
                && track.Filename != ""
            )
            .Select(track => new TrackLocation(track.Id, track.HostFolder!, track.Filename!))
            .ToListAsync(cancellationToken);

        List<Guid> missing =
        [
            .. locations
                .Where(location =>
                    !driver.FileExists(driver.CombinePath(location.HostFolder, location.Filename))
                )
                .Select(location => location.Id),
        ];

        if (missing.Count == 0)
            return 0;

        HashSet<Guid> heldByUsers = await UserHeldAsync(missing, cancellationToken);
        List<Guid> removable = [.. missing.Where(id => !heldByUsers.Contains(id))];

        if (removable.Count > 0)
            await RemoveAsync(removable, cancellationToken);

        logger.LogInformation(
            "Pruned {Removed} track row(s) of '{Root}' whose file is gone; kept {Kept} that a playlist or like still holds",
            removable.Count,
            scanRoot,
            heldByUsers.Count
        );

        return removable.Count;
    }

    private async Task<HashSet<Guid>> UserHeldAsync(
        List<Guid> trackIds,
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
