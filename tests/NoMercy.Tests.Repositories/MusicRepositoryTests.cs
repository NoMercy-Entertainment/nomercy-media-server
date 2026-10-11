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

[Trait("Category", "Unit")]
public class MusicRepositoryTests : IDisposable
{
    private readonly MediaContext _context;
    private readonly IDbContextFactory<MediaContext> _factory;
    private readonly SqliteConnection _connection;
    private readonly MusicRepository _repository;

    private static readonly Guid ArtistId1 = Guid.Parse("a0000001-0000-0000-0000-000000000001");
    private static readonly Guid ArtistId2 = Guid.Parse("a0000002-0000-0000-0000-000000000002");
    private static readonly Guid AlbumId1 = Guid.Parse("b0000001-0000-0000-0000-000000000001");
    private static readonly Guid AlbumId2 = Guid.Parse("b0000002-0000-0000-0000-000000000002");
    private static readonly Guid TrackId1 = Guid.Parse("c0000001-0000-0000-0000-000000000001");
    private static readonly Guid TrackId2 = Guid.Parse("c0000002-0000-0000-0000-000000000002");
    private static readonly Guid TrackId3 = Guid.Parse("c0000003-0000-0000-0000-000000000003");

    public MusicRepositoryTests()
    {
        (_factory, _connection) = TestMediaContextFactory.CreateFactory();
        _context = _factory.CreateDbContext();
        SeedMusicData(_context);
        _repository = new(_factory);
    }

    private static void SeedMusicData(MediaContext context)
    {
        // Phase 1: Base entities (no FK dependencies between each other)
        User testUser = new()
        {
            Id = SeedConstants.UserId,
            Email = "test@nomercy.tv",
            Name = "Test User",
            Owner = true,
            Allowed = true,
            Manage = true,
        };
        context.Users.Add(testUser);

        Library musicLibrary = new()
        {
            Id = SeedConstants.MusicLibraryId,
            Title = "Music",
            Type = "music",
            Order = 3,
        };
        context.Libraries.Add(musicLibrary);

        Driver systemLocalDriver = new()
        {
            Id = Driver.SystemLocalDriverId,
            Name = "Local Filesystem",
            Type = "local",
            Config = """{"rootPath":"/"}""",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        context.Drivers.Add(systemLocalDriver);

        Folder musicFolder = new()
        {
            Id = SeedConstants.MusicFolderId,
            Path = "/media/music",
            DriverId = Driver.SystemLocalDriverId,
        };
        context.Folders.Add(musicFolder);

        MusicGenre genre = new() { Id = Guid.NewGuid(), Name = "Rock" };
        context.MusicGenres.Add(genre);

        context.SaveChanges();

        // Phase 2: Entities with FKs to phase 1
        context.LibraryUser.Add(new(SeedConstants.MusicLibraryId, SeedConstants.UserId));
        context.FolderLibrary.Add(new(SeedConstants.MusicFolderId, SeedConstants.MusicLibraryId));

        Track track1 = new()
        {
            Id = TrackId1,
            Name = "Do I Wanna Know?",
            TrackNumber = 1,
            DiscNumber = 1,
            FolderId = SeedConstants.MusicFolderId,
            LibraryFolder = musicFolder,
        };
        Track track2 = new()
        {
            Id = TrackId2,
            Name = "R U Mine?",
            TrackNumber = 2,
            DiscNumber = 1,
            FolderId = SeedConstants.MusicFolderId,
            LibraryFolder = musicFolder,
        };
        Track track3 = new()
        {
            Id = TrackId3,
            Name = "Paranoid Android",
            TrackNumber = 1,
            DiscNumber = 1,
            FolderId = SeedConstants.MusicFolderId,
            LibraryFolder = musicFolder,
        };
        context.Tracks.AddRange([track1, track2, track3]);

        Artist artist1 = new()
        {
            Id = ArtistId1,
            Name = "Arctic Monkeys",
            Cover = "/arctic-monkeys.jpg",
            LibraryId = SeedConstants.MusicLibraryId,
            FolderId = SeedConstants.MusicFolderId,
            HostFolder = "/media/music/Arctic Monkeys",
            Library = musicLibrary,
            LibraryFolder = musicFolder,
        };
        Artist artist2 = new()
        {
            Id = ArtistId2,
            Name = "Radiohead",
            Cover = "/radiohead.jpg",
            LibraryId = SeedConstants.MusicLibraryId,
            FolderId = SeedConstants.MusicFolderId,
            HostFolder = "/media/music/Radiohead",
            Library = musicLibrary,
            LibraryFolder = musicFolder,
        };
        context.Artists.AddRange([artist1, artist2]);

        Album album1 = new()
        {
            Id = AlbumId1,
            Name = "AM",
            Cover = "/am.jpg",
            LibraryId = SeedConstants.MusicLibraryId,
            FolderId = SeedConstants.MusicFolderId,
            HostFolder = "/media/music/Arctic Monkeys/AM",
            Year = 2013,
            Library = musicLibrary,
            LibraryFolder = musicFolder,
        };
        Album album2 = new()
        {
            Id = AlbumId2,
            Name = "OK Computer",
            Cover = "/ok-computer.jpg",
            LibraryId = SeedConstants.MusicLibraryId,
            FolderId = SeedConstants.MusicFolderId,
            HostFolder = "/media/music/Radiohead/OK Computer",
            Year = 1997,
            Library = musicLibrary,
            LibraryFolder = musicFolder,
        };
        context.Albums.AddRange([album1, album2]);

        context.SaveChanges();

        // Phase 3: Join tables and play history
        context.AlbumTrack.AddRange([
            new AlbumTrack(AlbumId1, TrackId1),
            new AlbumTrack(AlbumId1, TrackId2),
            new AlbumTrack(AlbumId2, TrackId3),
        ]);

        context.ArtistTrack.AddRange([
            new ArtistTrack(ArtistId1, TrackId1),
            new ArtistTrack(ArtistId1, TrackId2),
            new ArtistTrack(ArtistId2, TrackId3),
        ]);

        context.AlbumArtist.AddRange([
            new AlbumArtist(AlbumId1, ArtistId1),
            new AlbumArtist(AlbumId2, ArtistId2),
        ]);

        context.ArtistUser.Add(new(ArtistId1, SeedConstants.UserId));
        context.AlbumUser.Add(new(AlbumId1, SeedConstants.UserId));
        context.TrackUser.Add(new(TrackId1, SeedConstants.UserId));

        context.MusicPlays.AddRange([
            new MusicPlay(SeedConstants.UserId, TrackId1),
            new MusicPlay(SeedConstants.UserId, TrackId1),
            new MusicPlay(SeedConstants.UserId, TrackId1),
            new MusicPlay(SeedConstants.UserId, TrackId3),
        ]);

        context.MusicGenreTrack.AddRange([
            new MusicGenreTrack(genre.Id, TrackId1),
            new MusicGenreTrack(genre.Id, TrackId2),
            new MusicGenreTrack(genre.Id, TrackId3),
        ]);

        context.SaveChanges();
    }

    #region Browsable Query Tests

    [Fact]
    public async Task GetArtists_ReturnsList_ThatCanBePaginated()
    {
        List<Artist> result =
        [
            .. (await _repository.GetArtists(SeedConstants.UserId, "A")).Take(1),
        ];

        Assert.Single(result);
        Assert.Equal("Arctic Monkeys", result[0].Name);
    }

    [Fact]
    public async Task GetArtists_ReturnsList_ThatCanBeFullyEnumerated()
    {
        List<Artist> result = await _repository.GetArtists(SeedConstants.UserId, "R");

        Assert.Single(result);
        Assert.Equal("Radiohead", result[0].Name);
    }

    [Fact]
    public async Task GetAlbums_ReturnsList_ThatCanBePaginated()
    {
        List<Album> result = [.. (await _repository.GetAlbums(SeedConstants.UserId, "A")).Take(1)];

        Assert.Single(result);
        Assert.Equal("AM", result[0].Name);
    }

    [Fact]
    public async Task GetTracks_ReturnsList_ForUserFavorites()
    {
        List<TrackUser> result = await _repository.GetTracks(SeedConstants.UserId);

        Assert.Single(result);
        Assert.Equal(TrackId1, result[0].TrackId);
    }

    [Fact]
    public async Task GetLatestAlbums_ReturnsList_ThatCanBePaginated()
    {
        List<Album> result = [.. (await _repository.GetLatestAlbums()).Take(1)];

        Assert.Single(result);
    }

    [Fact]
    public async Task GetLatestArtists_ReturnsList_ThatCanBePaginated()
    {
        List<Artist> result = [.. (await _repository.GetLatestArtists()).Take(1)];

        Assert.Single(result);
    }

    [Fact]
    public async Task GetLatestGenres_ReturnsList_OrderedByTrackCount()
    {
        List<MusicGenre> result = [.. (await _repository.GetLatestGenres()).Take(10)];

        Assert.Single(result);
        Assert.Equal("Rock", result[0].Name);
    }

    [Fact]
    public async Task GetFavoriteArtists_ReturnsList_ThatCanBePaginated()
    {
        List<ArtistUser> result =
        [
            .. (await _repository.GetFavoriteArtists(SeedConstants.UserId)).Take(36),
        ];

        Assert.Single(result);
        Assert.Equal(ArtistId1, result[0].ArtistId);
    }

    [Fact]
    public async Task GetFavoriteAlbums_ReturnsList_ThatCanBePaginated()
    {
        List<AlbumUser> result =
        [
            .. (await _repository.GetFavoriteAlbums(SeedConstants.UserId)).Take(36),
        ];

        Assert.Single(result);
        Assert.Equal(AlbumId1, result[0].AlbumId);
    }

    [Fact]
    public async Task GetFavoriteTracks_ReturnsList_ForUserFavorites()
    {
        List<TrackUser> result = await _repository.GetFavoriteTracks(SeedConstants.UserId);

        Assert.Single(result);
        Assert.Equal(TrackId1, result[0].TrackId);
    }

    #endregion

    #region Terminal Query Tests

    [Fact]
    public async Task GetFavoriteArtistAsync_ReturnsMaterializedList()
    {
        List<ArtistTrack> result = await _repository.GetFavoriteArtistAsync(SeedConstants.UserId);

        Assert.NotEmpty(result);
        Assert.Contains(result, at => at.ArtistId == ArtistId1);
    }

    [Fact]
    public async Task GetFavoriteArtistAsync_CanBeGroupedClientSide()
    {
        List<ArtistTrack> result = await _repository.GetFavoriteArtistAsync(SeedConstants.UserId);

        IGrouping<Guid, ArtistTrack>? topArtist = result
            .GroupBy(at => at.ArtistId)
            .MaxBy(g => g.Count());

        Assert.NotNull(topArtist);
        Assert.Equal(ArtistId1, topArtist.Key);
    }

    [Fact]
    public async Task GetFavoriteAlbumAsync_ReturnsMaterializedList()
    {
        List<AlbumTrack> result = await _repository.GetFavoriteAlbumAsync(SeedConstants.UserId);

        Assert.NotEmpty(result);
        Assert.Contains(result, at => at.AlbumId == AlbumId1);
    }

    [Fact]
    public async Task GetFavoritePlaylistAsync_ReturnsMaterializedList()
    {
        List<PlaylistTrack> result = await _repository.GetFavoritePlaylistAsync(
            SeedConstants.UserId
        );

        Assert.NotNull(result);
    }

    [Fact]
    public async Task GetFavoriteArtistAsync_ReturnsEmptyForUnknownUser()
    {
        List<ArtistTrack> result = await _repository.GetFavoriteArtistAsync(
            SeedConstants.OtherUserId
        );

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetFavoriteAlbumAsync_ReturnsEmptyForUnknownUser()
    {
        List<AlbumTrack> result = await _repository.GetFavoriteAlbumAsync(
            SeedConstants.OtherUserId
        );

        Assert.Empty(result);
    }

    #endregion

    #region No Disposed Context Tests

    [Fact]
    public async Task BrowsableQueries_DoNotThrowDisposedContextException()
    {
        List<Artist> artists = await _repository.GetArtists(SeedConstants.UserId, "_");
        List<Album> albums = await _repository.GetAlbums(SeedConstants.UserId, "_");
        List<TrackUser> tracks = await _repository.GetTracks(SeedConstants.UserId);

        Assert.NotNull(artists);
        Assert.NotNull(albums);
        Assert.NotNull(tracks);
    }

    // Stoney, 2026-10-06: "albums need their artist in the header".
    [Fact]
    public async Task AlbumCards_CarryTheirArtist()
    {
        List<AlbumCardDto> cards = await _repository.GetAlbumCardsByIdsAsync([AlbumId1]);
        MusicStartPageData start = await _repository.GetMusicStartPageAsync(SeedConstants.UserId);

        Assert.Equal("Arctic Monkeys", Assert.Single(cards).ArtistName);
        Assert.Contains(start.LatestAlbums, album => album.ArtistName == "Arctic Monkeys");
    }

    #endregion

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }
}
