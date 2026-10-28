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
using NoMercy.Database.Models.Music;

namespace NoMercy.MediaProcessing.Recordings;

public class RecordingRepository(MediaContext context) : IRecordingRepository
{
    public Task Store(Track recording, bool update = false)
    {
        if (update)
        {
            return context
                .Tracks.Upsert(recording)
                .On(e => new { e.Id })
                .WhenMatched(
                    (ts, ti) =>
                        new()
                        {
                            Id = ti.Id,
                            Name = ti.Name,
                            DiscNumber = ti.DiscNumber,
                            TrackNumber = ti.TrackNumber,
                            Date = ti.Date,
                            Folder = ts.Folder,
                            FolderId = ts.FolderId,
                            HostFolder = ts.HostFolder,
                            Duration = ts.Duration,
                            Filename = ts.Filename,
                            Quality = ts.Quality,
                        }
                )
                .RunAsync();
        }

        return context
            .Tracks.Upsert(recording)
            .On(e => new { e.Id })
            .WhenMatched(
                (ts, ti) =>
                    new()
                    {
                        Id = ti.Id,
                        Name = ti.Name,
                        DiscNumber = ti.DiscNumber,
                        TrackNumber = ti.TrackNumber,
                        Date = ti.Date,
                        Folder = ti.Folder,
                        FolderId = ti.FolderId,
                        HostFolder = ti.HostFolder,
                        Duration = ti.Duration,
                        Filename = ti.Filename,
                        Quality = ti.Quality,
                    }
            )
            .RunAsync();
    }

    public Task<int> RepointTrackFile(
        string fromHostFolder,
        string fromFilename,
        TrackFilePlacement to
    )
    {
        // ExecuteUpdate skips the entity setters, so the separators are normalized here.
        string fromHost = fromHostFolder.Replace('\\', '/');
        string toFolder = to.Folder.Replace('\\', '/');
        string toHost = to.HostFolder.Replace('\\', '/');

        return context
            .Tracks.Where(track => track.HostFolder == fromHost && track.Filename == fromFilename)
            .ExecuteUpdateAsync(setters =>
                setters
                    .SetProperty(track => track.FolderId, to.FolderId)
                    .SetProperty(track => track.Folder, toFolder)
                    .SetProperty(track => track.HostFolder, toHost)
                    .SetProperty(track => track.Filename, to.Filename)
            );
    }

    public Task LinkToRelease(AlbumTrack trackRelease)
    {
        return context
            .AlbumTrack.Upsert(trackRelease)
            .On(e => new { e.AlbumId, e.TrackId })
            .WhenMatched((s, i) => new() { AlbumId = i.AlbumId, TrackId = i.TrackId })
            .RunAsync();
    }

    public Task LinkToArtist(ArtistTrack insert)
    {
        return context
            .ArtistTrack.Upsert(insert)
            .On(e => new { e.ArtistId, e.TrackId })
            .WhenMatched((s, i) => new() { ArtistId = i.ArtistId, TrackId = i.TrackId })
            .RunAsync();
    }

    public Task LinkToLibrary(LibraryTrack libraryTrack)
    {
        return context
            .LibraryTrack.Upsert(new(libraryTrack.LibraryId, libraryTrack.TrackId))
            .On(v => new { v.LibraryId, v.TrackId })
            .WhenMatched((lts, lti) => new() { LibraryId = lti.LibraryId, TrackId = lti.TrackId })
            .RunAsync();
    }

    public Task LinkToLibrary(ArtistLibrary artistLibrary)
    {
        return context
            .ArtistLibrary.Upsert(new(artistLibrary.ArtistId, artistLibrary.LibraryId))
            .On(v => new { v.ArtistId, v.LibraryId })
            .WhenMatched((lts, lti) => new() { ArtistId = lti.ArtistId, LibraryId = lti.LibraryId })
            .RunAsync();
    }

    public async Task<int> UpdateTrackLyricsAsync(
        Track track,
        string serializeObject,
        string? source,
        string? timing
    )
    {
        return await context
            .Upsert(track)
            .On(v => new { v.Id })
            .WhenMatched(v =>
                new()
                {
                    _lyrics = serializeObject,
                    LyricsSource = source,
                    LyricsTiming = timing,
                    LyricsVersion = v.LyricsVersion + 1,
                }
            )
            .RunAsync();
    }
}
