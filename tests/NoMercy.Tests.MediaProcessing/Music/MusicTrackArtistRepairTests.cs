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

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NoMercy.Data.Jobs;
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Music;

namespace NoMercy.Tests.MediaProcessing.Music;

// Stoney, 2026-10-07: "howcome i am missing the artist on some including the one playing now?"
// The track repair stored the tracks it re-matched without their artists; 14 tracks in
// 7 folders were left with none.
public sealed class MusicTrackArtistRepairTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly MediaContext _context;

    public MusicTrackArtistRepairTests()
    {
        _connection = new("Data Source=:memory:");
        _connection.Open();

        using (SqliteCommand fkOff = _connection.CreateCommand())
        {
            fkOff.CommandText = "PRAGMA foreign_keys = OFF;";
            fkOff.ExecuteNonQuery();
        }

        DbContextOptions<MediaContext> options = new DbContextOptionsBuilder<MediaContext>()
            .UseSqlite(_connection)
            .Options;
        _context = new(options);
        _context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    private async Task<Track> SeedTrackAsync(
        Guid albumId,
        Ulid libraryId,
        Ulid folderId,
        string hostFolder,
        bool withArtist
    )
    {
        Track track = new()
        {
            Id = Guid.NewGuid(),
            Name = "Track",
            FolderId = folderId,
            HostFolder = hostFolder,
            LibraryFolder = null!,
            Metadata = null!,
        };
        _context.Tracks.Add(track);
        _context.AlbumTrack.Add(new() { AlbumId = albumId, TrackId = track.Id });
        _context.LibraryTrack.Add(new(libraryId, track.Id));
        if (withArtist)
            _context.ArtistTrack.Add(new() { ArtistId = Guid.NewGuid(), TrackId = track.Id });
        await _context.SaveChangesAsync();
        return track;
    }

    [Fact]
    public async Task Finds_OnlyTheFoldersOfTracksWithoutAnArtist_OncePerFolder()
    {
        Ulid libraryId = Ulid.NewUlid();
        Ulid folderId = Ulid.NewUlid();
        Guid nebraska = Guid.NewGuid();
        Guid complete = Guid.NewGuid();

        await SeedTrackAsync(
            nebraska,
            libraryId,
            folderId,
            "/Bruce Springsteen/Nebraska",
            withArtist: true
        );
        await SeedTrackAsync(
            nebraska,
            libraryId,
            folderId,
            "/Bruce Springsteen/Nebraska",
            withArtist: false
        );
        await SeedTrackAsync(
            nebraska,
            libraryId,
            folderId,
            "/Bruce Springsteen/Nebraska",
            withArtist: false
        );
        await SeedTrackAsync(
            complete,
            libraryId,
            folderId,
            "/Frank Sinatra/A Swingin' Affair!",
            withArtist: true
        );

        List<MusicTrackArtistRepairJob.RepairFolder> folders =
            await MusicTrackArtistRepairJob.FindFoldersAsync(_context);

        MusicTrackArtistRepairJob.RepairFolder folder = Assert.Single(folders);
        Assert.Equal(new(libraryId, folderId, nebraska, "/Bruce Springsteen/Nebraska"), folder);
    }
}
