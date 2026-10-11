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
using NoMercy.Database;
using NoMercy.MediaProcessing.Jobs;
using NoMercy.MediaProcessing.Jobs.MediaJobs;
using NoMercyQueue.Core.Interfaces;

namespace NoMercy.Data.Jobs;

/// <summary>
/// Gives tracks without an artist their artists back. The track repair used to store the
/// tracks it re-matched without their artists; this re-runs the (fixed) repair on only the
/// folders that hold such a track, so a library is not walked folder by folder for it.
/// </summary>
[Serializable]
public class MusicTrackArtistRepairJob : IShouldQueue
{
    public string QueueName => "import";
    public int Priority => 6;

    public record RepairFolder(Ulid LibraryId, Ulid FolderId, Guid AlbumId, string HostFolder);

    public async Task Handle()
    {
        await using MediaContext mediaContext = new();
        JobDispatcher jobDispatcher = new();

        foreach (RepairFolder folder in await FindFoldersAsync(mediaContext))
            jobDispatcher.DispatchJob<MusicTrackRepairJob>(
                folder.LibraryId,
                folder.FolderId,
                folder.AlbumId,
                folder.HostFolder
            );
    }

    public static async Task<List<RepairFolder>> FindFoldersAsync(MediaContext mediaContext)
    {
        var rows = await mediaContext
            .Tracks.AsNoTracking()
            .Where(track => !track.ArtistTrack.Any() && track.HostFolder != null)
            .SelectMany(
                track => track.AlbumTrack,
                (track, albumTrack) =>
                    new
                    {
                        track.FolderId,
                        HostFolder = track.HostFolder!,
                        albumTrack.AlbumId,
                        LibraryId = track
                            .LibraryTrack.Select(link => link.LibraryId)
                            .FirstOrDefault(),
                    }
            )
            .Distinct()
            .ToListAsync();

        return
        [
            .. rows.Select(row => new RepairFolder(
                row.LibraryId,
                row.FolderId,
                row.AlbumId,
                row.HostFolder
            )),
        ];
    }
}
