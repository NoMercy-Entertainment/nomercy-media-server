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
using NoMercy.Data.Repositories;
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Music;
using NoMercy.Database.Models.Storage;
using NoMercy.Database.Models.Users;
using NoMercy.Tests.Repositories.Infrastructure;

namespace NoMercy.Tests.Repositories;

/// <summary>
/// A track whose file went missing (MissingSince set) stays in the database but must
/// leave every list and count a user sees, until the file returns.
/// </summary>
[Trait("Category", "Unit")]
public class MusicMissingTrackVisibilityTests : IDisposable
{
    private readonly MediaContext _context;
    private readonly IDbContextFactory<MediaContext> _factory;
    private readonly SqliteConnection _connection;
    private readonly MusicRepository _repository;

    private readonly Guid _albumId = Guid.NewGuid();
    private readonly Guid _artistId = Guid.NewGuid();
    private readonly Guid _playlistId = Guid.NewGuid();
    private readonly Guid _presentTrackId = Guid.NewGuid();
    private readonly Guid _missingTrackId = Guid.NewGuid();

    public MusicMissingTrackVisibilityTests()
    {
        (_factory, _connection) = TestMediaContextFactory.CreateFactory();
        _context = _factory.CreateDbContext();
        _repository = new(_factory);
        Seed();
    }

    private void Seed()
    {
        User user = new()
        {
            Id = SeedConstants.UserId,
            Email = "listener@example.test",
            Name = "Test Listener",
            Owner = true,
            Allowed = true,
            Manage = true,
        };
        _context.Users.Add(user);

        Library library = new()
        {
            Id = Ulid.NewUlid(),
            Title = "Music",
            Type = "music",
            Order = 3,
        };
        _context.Libraries.Add(library);

        if (!_context.Drivers.Any(d => d.Id == Driver.SystemLocalDriverId))
            _context.Drivers.Add(
                new()
                {
                    Id = Driver.SystemLocalDriverId,
                    Name = "Local Filesystem",
                    Type = "local",
                    Config = """{"rootPath":"/"}""",
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                }
            );

        Folder folder = new()
        {
            Id = Ulid.NewUlid(),
            Path = "/media/music",
            DriverId = Driver.SystemLocalDriverId,
        };
        _context.Folders.Add(folder);
        _context.SaveChanges();

        _context.LibraryUser.Add(new(library.Id, SeedConstants.UserId));
        _context.FolderLibrary.Add(new(folder.Id, library.Id));
        _context.SaveChanges();

        Artist artist = new()
        {
            Id = _artistId,
            Name = "Example Artist",
            LibraryId = library.Id,
            FolderId = folder.Id,
            HostFolder = "/media/music/Example Artist",
            Library = library,
            LibraryFolder = folder,
        };
        _context.Artists.Add(artist);

        Album album = new()
        {
            Id = _albumId,
            Name = "Example Album",
            Cover = "/cover.jpg",
            LibraryId = library.Id,
            FolderId = folder.Id,
            HostFolder = "/media/music/Example Artist/Example Album",
            Year = 2020,
            Library = library,
            LibraryFolder = folder,
        };
        _context.Albums.Add(album);

        Track present = new()
        {
            Id = _presentTrackId,
            Name = "Zebra Present",
            TrackNumber = 1,
            DiscNumber = 1,
            Duration = "00:03:00",
            FolderId = folder.Id,
            LibraryFolder = folder,
        };
        Track missing = new()
        {
            Id = _missingTrackId,
            Name = "Zebra Missing",
            TrackNumber = 2,
            DiscNumber = 1,
            Duration = "00:03:00",
            FolderId = folder.Id,
            LibraryFolder = folder,
            MissingSince = DateTime.UtcNow.AddDays(-1),
        };
        _context.Tracks.AddRange([present, missing]);
        _context.SaveChanges();

        _context.AlbumTrack.AddRange([
            new AlbumTrack(_albumId, _presentTrackId),
            new AlbumTrack(_albumId, _missingTrackId),
        ]);
        _context.ArtistTrack.AddRange([
            new ArtistTrack(_artistId, _presentTrackId),
            new ArtistTrack(_artistId, _missingTrackId),
        ]);
        _context.AlbumArtist.Add(new(_albumId, _artistId));

        Playlist playlist = new()
        {
            Id = _playlistId,
            Name = "Example Playlist",
            UserId = SeedConstants.UserId,
        };
        _context.Playlists.Add(playlist);
        _context.SaveChanges();

        _context.PlaylistTrack.AddRange([
            new PlaylistTrack(_playlistId, _presentTrackId),
            new PlaylistTrack(_playlistId, _missingTrackId),
        ]);
        _context.SaveChanges();
    }

    [Fact]
    public async Task Album_tracks_leave_out_a_track_whose_file_is_missing()
    {
        List<AlbumTrack> tracks = await _repository.GetAlbumTracksAsync(
            SeedConstants.UserId,
            _albumId
        );

        Assert.Equal([_presentTrackId], tracks.Select(at => at.TrackId).ToList());
    }

    [Fact]
    public async Task Track_search_leaves_out_a_track_whose_file_is_missing()
    {
        List<Guid> ids = await _repository.SearchTrackIdsAsync("zebra");

        Assert.Equal([_presentTrackId], ids);
    }

    [Fact]
    public async Task Playlist_tracks_leave_out_a_track_whose_file_is_missing()
    {
        List<PlaylistTrack> tracks = await _repository.GetPlaylistTracksAsync(
            SeedConstants.UserId,
            _playlistId
        );

        Assert.Equal([_presentTrackId], tracks.Select(pt => pt.TrackId).ToList());
    }

    [Fact]
    public async Task Album_page_leaves_out_a_track_whose_file_is_missing()
    {
        Album? album = await _repository.GetAlbumAsync(SeedConstants.UserId, _albumId);

        Assert.NotNull(album);
        Assert.Equal([_presentTrackId], album.AlbumTrack.Select(at => at.TrackId).ToList());
    }

    [Fact]
    public async Task Album_cards_count_only_tracks_whose_file_is_present()
    {
        List<AlbumCardDto> byIds = await _repository.GetAlbumCardsByIdsAsync([_albumId]);
        List<AlbumCardDto> all = await _repository.GetAllAlbumCardsAsync(
            SeedConstants.UserId,
            "en"
        );
        List<AlbumCardDto> latest = await _repository.GetLatestAlbumCardsAsync();

        Assert.Equal(1, Assert.Single(byIds).TrackCount);
        Assert.Equal(1, Assert.Single(all).TrackCount);
        Assert.Equal(1, Assert.Single(latest).TrackCount);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }
}
