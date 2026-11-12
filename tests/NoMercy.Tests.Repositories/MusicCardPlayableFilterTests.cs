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

using FluentAssertions;
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
/// An artist or album card needs a playable track. One with only missing tracks
/// stays in the database but leaves search, favorites, most-played and the artist page.
/// </summary>
[Trait("Category", "Unit")]
public class MusicCardPlayableFilterTests : IDisposable
{
    private readonly MediaContext _context;
    private readonly SqliteConnection _connection;
    private readonly MusicRepository _repository;

    private readonly Guid _playableArtist = Guid.NewGuid();
    private readonly Guid _ghostArtist = Guid.NewGuid();
    private readonly Guid _playableAlbum = Guid.NewGuid();
    private readonly Guid _ghostAlbum = Guid.NewGuid();

    public MusicCardPlayableFilterTests()
    {
        (IDbContextFactory<MediaContext> factory, _connection) =
            TestMediaContextFactory.CreateFactory();
        _context = factory.CreateDbContext();
        _repository = new(factory);
        Seed();
    }

    private void Seed()
    {
        _context.Users.Add(
            new()
            {
                Id = SeedConstants.UserId,
                Email = "listener@example.test",
                Name = "Test Listener",
                Owner = true,
                Allowed = true,
                Manage = true,
            }
        );
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

        foreach (
            (Guid id, string name) in new[]
            {
                (_playableArtist, "Alpha Artist"),
                (_ghostArtist, "Ghost Artist"),
            }
        )
        {
            _context.Artists.Add(
                new()
                {
                    Id = id,
                    Name = name,
                    LibraryId = library.Id,
                    FolderId = folder.Id,
                    HostFolder = $"/media/music/{name}",
                    Library = library,
                    LibraryFolder = folder,
                }
            );
        }
        foreach (
            (Guid id, string name) in new[]
            {
                (_playableAlbum, "Alpha Album"),
                (_ghostAlbum, "Ghost Album"),
            }
        )
        {
            _context.Albums.Add(
                new()
                {
                    Id = id,
                    Name = name,
                    Cover = "/cover.jpg",
                    LibraryId = library.Id,
                    FolderId = folder.Id,
                    HostFolder = $"/media/music/{name}",
                    Year = 2020,
                    Library = library,
                    LibraryFolder = folder,
                }
            );
        }

        Track present = NewTrack("Alpha Present", folder, missing: false);
        Track ghost = NewTrack("Ghost Missing", folder, missing: true);
        _context.Tracks.AddRange([present, ghost]);
        _context.SaveChanges();

        _context.ArtistTrack.AddRange([
            new ArtistTrack(_playableArtist, present.Id),
            new ArtistTrack(_ghostArtist, ghost.Id),
        ]);
        _context.AlbumTrack.AddRange([
            new AlbumTrack(_playableAlbum, present.Id),
            new AlbumTrack(_ghostAlbum, ghost.Id),
        ]);
        _context.AlbumArtist.AddRange([
            new AlbumArtist(_playableAlbum, _playableArtist),
            new AlbumArtist(_ghostAlbum, _playableArtist),
            new AlbumArtist(_ghostAlbum, _ghostArtist),
        ]);
        _context.ArtistUser.AddRange([
            new ArtistUser(_playableArtist, SeedConstants.UserId),
            new ArtistUser(_ghostArtist, SeedConstants.UserId),
        ]);
        _context.AlbumUser.AddRange([
            new AlbumUser(_playableAlbum, SeedConstants.UserId),
            new AlbumUser(_ghostAlbum, SeedConstants.UserId),
        ]);
        // The missing track is played most, so an unfiltered ranking puts it first.
        _context.MusicPlays.AddRange([
            new MusicPlay(SeedConstants.UserId, ghost.Id),
            new MusicPlay(SeedConstants.UserId, ghost.Id),
            new MusicPlay(SeedConstants.UserId, ghost.Id),
            new MusicPlay(SeedConstants.UserId, present.Id),
        ]);
        _context.SaveChanges();
    }

    private static Track NewTrack(string name, Folder folder, bool missing)
    {
        return new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            TrackNumber = 1,
            DiscNumber = 1,
            Duration = "00:03:00",
            FolderId = folder.Id,
            LibraryFolder = folder,
            MissingSince = missing ? DateTime.UtcNow.AddDays(-1) : null,
        };
    }

    [Fact]
    public async Task Search_ids_drop_an_artist_and_an_album_with_no_playable_track()
    {
        List<Guid> artists = await _repository.SearchArtistIdsAsync("artist", SeedConstants.UserId);
        List<Guid> albums = await _repository.SearchAlbumIdsAsync("album", SeedConstants.UserId);

        artists.Should().Equal(_playableArtist);
        albums.Should().Equal(_playableAlbum);
    }

    [Fact]
    public async Task Cards_by_ids_drop_an_artist_and_an_album_with_no_playable_track()
    {
        List<ArtistCardDto> artists = await _repository.GetArtistCardsByIdsAsync(
            [_playableArtist, _ghostArtist],
            SeedConstants.UserId
        );
        List<AlbumCardDto> albums = await _repository.GetAlbumCardsByIdsAsync(
            [_playableAlbum, _ghostAlbum],
            SeedConstants.UserId
        );

        artists.Select(a => a.Id).Should().Equal(_playableArtist);
        albums.Select(a => a.Id).Should().Equal(_playableAlbum);
    }

    [Fact]
    public async Task Favorites_drop_an_artist_and_an_album_with_no_playable_track()
    {
        List<ArtistCardDto> artists = await _repository.GetFavoriteArtistCardsAsync(
            SeedConstants.UserId
        );
        List<AlbumCardDto> albums = await _repository.GetFavoriteAlbumCardsAsync(
            SeedConstants.UserId
        );
        MusicStartPageData start = await _repository.GetMusicStartPageAsync(SeedConstants.UserId);

        artists.Select(a => a.Id).Should().Equal(_playableArtist);
        albums.Select(a => a.Id).Should().Equal(_playableAlbum);
        start.FavoriteArtists.Select(a => a.Id).Should().Equal(_playableArtist);
        start.FavoriteAlbums.Select(a => a.Id).Should().Equal(_playableAlbum);
    }

    [Fact]
    public async Task Most_played_skips_an_artist_and_an_album_with_no_playable_track()
    {
        MusicStartPageData start = await _repository.GetMusicStartPageAsync(SeedConstants.UserId);

        start.TopArtist.Should().NotBeNull();
        start.TopAlbum.Should().NotBeNull();
        Guid.Parse(start.TopArtist!.Id).Should().Be(_playableArtist);
        Guid.Parse(start.TopAlbum!.Id).Should().Be(_playableAlbum);
    }

    [Fact]
    public async Task Artist_page_lists_only_albums_with_a_playable_track()
    {
        Artist? artist = await _repository.GetArtistAsync(SeedConstants.UserId, _playableArtist);

        artist.Should().NotBeNull();
        artist!.AlbumArtist.Select(link => link.AlbumId).Should().Equal(_playableAlbum);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }
}
