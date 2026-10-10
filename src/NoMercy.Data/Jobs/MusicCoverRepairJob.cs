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
using Newtonsoft.Json;
using NoMercy.Database;
using NoMercy.MediaProcessing.Images;
using NoMercyQueue;
using NoMercyQueue.Core.Interfaces;

namespace NoMercy.Data.Jobs;

/// <summary>
/// Re-fetches the art of every artist and album whose <c>Cover</c> names a file
/// that is not in the music images folder. Older importers wrote the name
/// without downloading the file, so those covers answer 404 for good; the fetch
/// jobs only write a cover name once the file is stored, so running them again
/// puts a real file behind the name.
/// </summary>
[Serializable]
public class MusicCoverRepairJob : IShouldQueue
{
    public string QueueName => "image";
    public int Priority => 3;

    [JsonIgnore]
    public MusicCoverFiles CoverFiles { get; set; } = new();

    public async Task Handle()
    {
        await using MediaContext mediaContext = new();
        List<IShouldQueue> jobs = await FindRepairJobsAsync(mediaContext);

        foreach (IShouldQueue job in jobs)
            QueueRunner.Current!.Dispatcher.Dispatch(job);
    }

    public async Task<List<IShouldQueue>> FindRepairJobsAsync(MediaContext mediaContext)
    {
        List<IShouldQueue> jobs = [];

        var artists = await mediaContext
            .Artists.AsNoTracking()
            .Where(a => a.Cover != null && a.Cover != "")
            .Select(a => new { a.Id, Cover = a.Cover! })
            .ToListAsync();

        foreach (var artist in artists)
            if (!await CoverFiles.IsStoredAsync(artist.Cover))
                jobs.Add(new FanArtImagesJob { ArtistId = artist.Id });

        var albums = await mediaContext
            .Albums.AsNoTracking()
            .Where(a => a.Cover != null && a.Cover != "")
            .Select(a => new { a.Id, Cover = a.Cover! })
            .ToListAsync();

        List<Guid> missingAlbumIds = [];
        foreach (var album in albums)
            if (!await CoverFiles.IsStoredAsync(album.Cover))
                missingAlbumIds.Add(album.Id);

        List<Guid> releaseGroupIds = await mediaContext
            .AlbumReleaseGroup.AsNoTracking()
            .Where(arg => missingAlbumIds.Contains(arg.AlbumId))
            .Select(arg => arg.ReleaseGroupId)
            .Distinct()
            .ToListAsync();

        foreach (Guid albumId in missingAlbumIds)
            jobs.Add(new CoverArtImageJob { ReleaseId = albumId, HasFrontCover = true });

        foreach (Guid releaseGroupId in releaseGroupIds)
            jobs.Add(new FanArtImagesJob { ReleaseGroupId = releaseGroupId });

        return jobs;
    }
}
