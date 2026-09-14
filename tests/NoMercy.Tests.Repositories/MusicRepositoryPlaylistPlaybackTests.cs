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
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NoMercy.Data.Repositories;
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Music;
using NoMercy.Database.Models.Storage;
using NoMercy.Database.Models.Users;
using NoMercy.Tests.Repositories.Infrastructure;
using Xunit.Abstractions;

namespace NoMercy.Tests.Repositories;

/// <summary>
/// Regression coverage for the MusicRepository.Playlists.cs query-shape bugs found via
/// live-server verification: (1) GetPlaylistTrackAsync's AsNoTracking() Include revisited
/// PlaylistTrack through Playlist.Tracks, which EF Core's no-tracking validator rejects
/// unconditionally as a cycle — every playlist playback start 500'd. (2) GetArtistTrackAsync
/// chained three collection navigations (ArtistTrack, AlbumTrack, Translations) behind a
/// single-row filter, which took 80-100 seconds for a 153-track artist on SQLite. (3) and (4)
/// GetAlbumTrackAsync and GetGenreTrackAsync carried the identical cyclic-Include shape
/// (Album.AlbumTrack / Genre.MusicGenreTracks revisiting the query root) and only avoided the
/// no-tracking validator because they ran tracked — an accident, not a design, and one that
/// still forced the same split-query correlation cost as the artist bug.
/// </summary>
[Trait("Category", "Unit")]
public class MusicRepositoryPlaylistPlaybackTests : IDisposable
{
    private readonly MediaContext _context;
    private readonly IDbContextFactory<MediaContext> _factory;
    private readonly SqliteConnection _connection;
    private readonly MusicRepository _repository;
    private readonly ITestOutputHelper _output;

    private static readonly Guid OtherUserId = Guid.Parse("d0000001-0000-0000-0000-000000000001");

    public MusicRepositoryPlaylistPlaybackTests(ITestOutputHelper output)
    {
        _output = output;
        (_factory, _connection) = TestMediaContextFactory.CreateFactory();
        _context = _factory.CreateDbContext();
        _repository = new(_factory);
    }

    private static (Library library, Folder folder) SeedLibraryAndFolder(
        MediaContext context,
        Guid userId
    )
    {
        User testUser = new()
        {
            Id = userId,
            Email = $"{userId}@nomercy.tv",
            Name = "Test User",
            Owner = true,
            Allowed = true,
            Manage = true,
        };
        context.Users.Add(testUser);

        Library musicLibrary = new()
        {
            Id = Ulid.NewUlid(),
            Title = "Music",
            Type = "music",
            Order = 3,
        };
        context.Libraries.Add(musicLibrary);

        Driver driver = new()
        {
            Id = Driver.SystemLocalDriverId,
            Name = "Local Filesystem",
            Type = "local",
            Config = """{"rootPath":"/"}""",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        if (!context.Drivers.Any(d => d.Id == Driver.SystemLocalDriverId))
            context.Drivers.Add(driver);

        Folder musicFolder = new()
        {
            Id = Ulid.NewUlid(),
            Path = "/media/music",
            DriverId = Driver.SystemLocalDriverId,
        };
        context.Folders.Add(musicFolder);

        context.SaveChanges();

        context.LibraryUser.Add(new(musicLibrary.Id, userId));
        context.FolderLibrary.Add(new(musicFolder.Id, musicLibrary.Id));
        context.SaveChanges();

        return (musicLibrary, musicFolder);
    }

    #region Bug 1 — cyclic Include on playlist playback start

    [Fact]
    public async Task GetPlaylistTracksAsync_ReturnsFullOrderedList_WithoutCyclicIncludeCrash()
    {
        (Library library, Folder folder) = SeedLibraryAndFolder(_context, SeedConstants.UserId);

        Artist artist = new()
        {
            Id = Guid.NewGuid(),
            Name = "Arctic Monkeys",
            LibraryId = library.Id,
            FolderId = folder.Id,
            HostFolder = "/media/music/Arctic Monkeys",
            Library = library,
            LibraryFolder = folder,
        };
        _context.Artists.Add(artist);

        Album album = new()
        {
            Id = Guid.NewGuid(),
            Name = "AM",
            Cover = "/am.jpg",
            LibraryId = library.Id,
            FolderId = folder.Id,
            HostFolder = "/media/music/Arctic Monkeys/AM",
            Year = 2013,
            Library = library,
            LibraryFolder = folder,
        };
        _context.Albums.Add(album);

        Track trackA = new()
        {
            Id = Guid.NewGuid(),
            Name = "Do I Wanna Know?",
            TrackNumber = 1,
            DiscNumber = 1,
            FolderId = folder.Id,
            LibraryFolder = folder,
        };
        Track trackB = new()
        {
            Id = Guid.NewGuid(),
            Name = "R U Mine?",
            TrackNumber = 2,
            DiscNumber = 1,
            FolderId = folder.Id,
            LibraryFolder = folder,
        };
        Track trackC = new()
        {
            Id = Guid.NewGuid(),
            Name = "Arabella",
            TrackNumber = 3,
            DiscNumber = 1,
            FolderId = folder.Id,
            LibraryFolder = folder,
        };
        _context.Tracks.AddRange([trackA, trackB, trackC]);
        _context.SaveChanges();

        _context.AlbumTrack.AddRange([
            new AlbumTrack(album.Id, trackA.Id),
            new AlbumTrack(album.Id, trackB.Id),
            new AlbumTrack(album.Id, trackC.Id),
        ]);
        _context.ArtistTrack.AddRange([
            new ArtistTrack(artist.Id, trackA.Id),
            new ArtistTrack(artist.Id, trackB.Id),
            new ArtistTrack(artist.Id, trackC.Id),
        ]);
        _context.AlbumArtist.Add(new(album.Id, artist.Id));

        Playlist playlist = new()
        {
            Id = Guid.NewGuid(),
            Name = "My Playlist",
            UserId = SeedConstants.UserId,
        };
        _context.Playlists.Add(playlist);
        _context.SaveChanges();

        _context.PlaylistTrack.AddRange([
            new PlaylistTrack(playlist.Id, trackA.Id),
            new PlaylistTrack(playlist.Id, trackB.Id),
            new PlaylistTrack(playlist.Id, trackC.Id),
        ]);
        _context.SaveChanges();

        List<PlaylistTrack> result = await _repository.GetPlaylistTracksAsync(
            SeedConstants.UserId,
            playlist.Id
        );

        Assert.Equal(3, result.Count);

        PlaylistTrack? target = result.FirstOrDefault(pt => pt.TrackId == trackB.Id);
        Assert.NotNull(target);
        Assert.Equal("R U Mine?", target!.Track.Name);
        Assert.Equal("AM", target.Track.AlbumTrack.First().Album.Name);
        Assert.Equal("Arctic Monkeys", target.Track.ArtistTrack.First().Artist.Name);
    }

    [Fact]
    public async Task GetPlaylistTracksAsync_ReturnsEmpty_WhenPlaylistNotOwnedByCaller()
    {
        (Library library, Folder folder) = SeedLibraryAndFolder(_context, SeedConstants.UserId);
        _context.Users.Add(
            new()
            {
                Id = OtherUserId,
                Email = "other@nomercy.tv",
                Name = "Other User",
                Owner = false,
                Allowed = true,
                Manage = false,
            }
        );

        Track track = new()
        {
            Id = Guid.NewGuid(),
            Name = "Solo Track",
            TrackNumber = 1,
            DiscNumber = 1,
            FolderId = folder.Id,
            LibraryFolder = folder,
        };
        _context.Tracks.Add(track);
        _context.SaveChanges();

        Playlist playlist = new()
        {
            Id = Guid.NewGuid(),
            Name = "Someone Else's Playlist",
            UserId = OtherUserId,
        };
        _context.Playlists.Add(playlist);
        _context.SaveChanges();

        _context.PlaylistTrack.Add(new(playlist.Id, track.Id));
        _context.SaveChanges();

        // Requesting as SeedConstants.UserId, who does not own this playlist.
        List<PlaylistTrack> result = await _repository.GetPlaylistTracksAsync(
            SeedConstants.UserId,
            playlist.Id
        );

        Assert.Empty(result);
    }

    [Fact]
    public async Task IsTrackFavoriteAsync_IsTrueOnlyForTheUserWhoLikedIt()
    {
        (Library library, Folder folder) = SeedLibraryAndFolder(_context, SeedConstants.UserId);
        Track track = new()
        {
            Id = Guid.NewGuid(),
            Name = "Liked Track",
            TrackNumber = 1,
            DiscNumber = 1,
            FolderId = folder.Id,
            LibraryFolder = folder,
        };
        _context.Tracks.Add(track);
        _context.SaveChanges();
        _context.TrackUser.Add(new() { TrackId = track.Id, UserId = SeedConstants.UserId });
        _context.SaveChanges();

        Assert.True(await _repository.IsTrackFavoriteAsync(track.Id, SeedConstants.UserId));
        Assert.False(await _repository.IsTrackFavoriteAsync(track.Id, Guid.NewGuid()));
    }

    /// <summary>
    /// Reproduces the exact shape of the pre-fix query directly against this harness's real
    /// SQLite + real EF Core, proving the cycle exception is a genuine, unconditional
    /// expression-tree-shape check (not data-dependent) and that this test infra exercises it.
    /// If this ever stops throwing, EF Core's validator changed underneath us — re-verify
    /// GetPlaylistTracksAsync is still needed in its current (non-cyclic) shape.
    /// </summary>
    [Fact]
    public async Task OldCyclicPlaylistIncludeShape_StillThrowsInvalidOperationException()
    {
        (Library library, Folder folder) = SeedLibraryAndFolder(_context, SeedConstants.UserId);

        Track track = new()
        {
            Id = Guid.NewGuid(),
            Name = "Track",
            TrackNumber = 1,
            DiscNumber = 1,
            FolderId = folder.Id,
            LibraryFolder = folder,
        };
        _context.Tracks.Add(track);
        _context.SaveChanges();

        Playlist playlist = new()
        {
            Id = Guid.NewGuid(),
            Name = "Playlist",
            UserId = SeedConstants.UserId,
        };
        _context.Playlists.Add(playlist);
        _context.SaveChanges();

        _context.PlaylistTrack.Add(new(playlist.Id, track.Id));
        _context.SaveChanges();

        await using MediaContext queryContext = await _factory.CreateDbContextAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            queryContext
                .PlaylistTrack.AsNoTracking()
                .Where(pt => pt.PlaylistId == playlist.Id && pt.TrackId == track.Id)
                .Include(pt => pt.Playlist)
                    .ThenInclude(p => p.Tracks)
                        .ThenInclude(playlistTrack => playlistTrack.Track)
                .FirstOrDefaultAsync()
        );
    }

    #endregion

    #region Bug 2 — artist playback performance

    [Fact]
    public async Task GetArtistTracksAsync_CompletesWellUnderTwoSeconds_For150PlusTracks()
    {
        (Library library, Folder folder) = SeedLibraryAndFolder(_context, SeedConstants.UserId);

        Artist artist = new()
        {
            Id = Guid.NewGuid(),
            Name = "Radiohead",
            LibraryId = library.Id,
            FolderId = folder.Id,
            HostFolder = "/media/music/Radiohead",
            Library = library,
            LibraryFolder = folder,
        };
        _context.Artists.Add(artist);
        _context.SaveChanges();

        _context.Images.Add(
            new()
            {
                ArtistId = artist.Id,
                Type = "background",
                FilePath = "/radiohead-bg.jpg",
                AspectRatio = 1.78,
            }
        );

        const int albumCount = 5;
        const int tracksPerAlbum = 31; // 155 tracks total, matching the live-log scale (153)
        List<Album> albums = [];
        for (int a = 0; a < albumCount; a++)
        {
            Album album = new()
            {
                Id = Guid.NewGuid(),
                Name = $"Album {a}",
                Cover = $"/album-{a}.jpg",
                LibraryId = library.Id,
                FolderId = folder.Id,
                HostFolder = $"/media/music/Radiohead/Album {a}",
                Year = 1995 + a,
                Library = library,
                LibraryFolder = folder,
            };
            albums.Add(album);
        }
        _context.Albums.AddRange(albums);
        _context.SaveChanges();

        foreach (Album album in albums)
        {
            _context.AlbumArtist.Add(new(album.Id, artist.Id));
            for (int iso = 0; iso < 3; iso++)
            {
                _context.Translations.Add(
                    new()
                    {
                        AlbumId = album.Id,
                        Iso31661 = iso switch
                        {
                            0 => "US",
                            1 => "GB",
                            _ => "NL",
                        },
                        Description = $"{album.Name} description {iso}",
                    }
                );
            }
        }

        List<Track> tracks = [];
        for (int a = 0; a < albumCount; a++)
        for (int t = 0; t < tracksPerAlbum; t++)
        {
            Track track = new()
            {
                Id = Guid.NewGuid(),
                Name = $"Track {a}-{t}",
                TrackNumber = t + 1,
                DiscNumber = 1,
                FolderId = folder.Id,
                LibraryFolder = folder,
            };
            tracks.Add(track);
        }
        _context.Tracks.AddRange(tracks);
        _context.SaveChanges();

        int trackIndex = 0;
        for (int a = 0; a < albumCount; a++)
        for (int t = 0; t < tracksPerAlbum; t++)
        {
            Track track = tracks[trackIndex++];
            _context.AlbumTrack.Add(new(albums[a].Id, track.Id));
            _context.ArtistTrack.Add(new(artist.Id, track.Id));
        }
        _context.SaveChanges();

        Assert.Equal(albumCount * tracksPerAlbum, tracks.Count);

        Stopwatch stopwatch = Stopwatch.StartNew();
        List<ArtistTrack> result = await _repository.GetArtistTracksAsync(
            SeedConstants.UserId,
            artist.Id
        );
        stopwatch.Stop();
        _output.WriteLine(
            $"GetArtistTracksAsync elapsed {stopwatch.ElapsedMilliseconds}ms for {result.Count} tracks "
                + "(live-log regression was 81168/62660/103197ms for 153 tracks)"
        );

        Assert.Equal(albumCount * tracksPerAlbum, result.Count);
        // Prove the two-step attach actually populated the per-track credit list
        // (Track.ArtistTrack), not just the flat first-query columns.
        Assert.All(result, at => Assert.NotEmpty(at.Track.ArtistTrack));
        Assert.All(result, at => Assert.NotEmpty(at.Track.AlbumTrack));

        // The regression measured live was 80,000-103,000ms for 153 tracks.
        // 2000ms leaves generous CI headroom while still proving the fix by
        // roughly two orders of magnitude.
        Assert.True(
            stopwatch.ElapsedMilliseconds < 2000,
            $"GetArtistTracksAsync took {stopwatch.ElapsedMilliseconds}ms for {result.Count} tracks — expected < 2000ms"
        );
    }

    /// <summary>
    /// Regression for the live-incident follow-up: GetArtistTracksAsync used to join
    /// Album.Translations by rooting through EVERY track (Track -&gt; AlbumTrack -&gt; Album),
    /// re-deriving each of the artist's albums' Translations once PER TRACK on that album
    /// instead of once per distinct album — the SAME bug shape fixed for
    /// GetAlbumTracksAsync, except worse live (measured 80-103s for a 153-track artist,
    /// against 15-19s for a single 38-track album) because an artist's tracks span many
    /// distinct albums, each re-derived repeatedly. The fix roots the Translations fetch at
    /// Album directly (WHERE Id IN distinctAlbumIds), so the query count stays flat
    /// regardless of how many tracks — or how many distinct albums — the artist has.
    /// </summary>
    [Fact]
    public async Task GetArtistTracksAsync_QueriesAlbumTranslations_OncePerDistinctAlbum_NotOncePerTrack()
    {
        SqlCaptureInterceptor interceptor = new();
        (IDbContextFactory<MediaContext> factory, SqliteConnection connection) =
            TestMediaContextFactory.CreateFactoryWithInterceptor(interceptor);
        await using MediaContext context = factory.CreateDbContext();

        (Library library, Folder folder) = SeedLibraryAndFolder(context, SeedConstants.UserId);

        Artist artist = new()
        {
            Id = Guid.NewGuid(),
            Name = "Amy Winehouse",
            LibraryId = library.Id,
            FolderId = folder.Id,
            HostFolder = "/media/music/Amy Winehouse",
            Library = library,
            LibraryFolder = folder,
        };
        context.Artists.Add(artist);
        context.SaveChanges();

        const int albumCount = 5;
        const int tracksPerAlbum = 31; // 155 tracks total, matching the live 153-track scale
        List<Album> albums = [];
        for (int a = 0; a < albumCount; a++)
        {
            Album album = new()
            {
                Id = Guid.NewGuid(),
                Name = $"Album {a}",
                LibraryId = library.Id,
                FolderId = folder.Id,
                HostFolder = $"/media/music/Amy Winehouse/Album {a}",
                Year = 2000 + a,
                Library = library,
                LibraryFolder = folder,
            };
            albums.Add(album);
        }
        context.Albums.AddRange(albums);
        context.SaveChanges();

        foreach (Album album in albums)
            context.Translations.Add(
                new()
                {
                    AlbumId = album.Id,
                    Iso31661 = "US",
                    Description = $"{album.Name} description",
                }
            );
        context.SaveChanges();

        List<Track> tracks = [];
        for (int a = 0; a < albumCount; a++)
        for (int t = 0; t < tracksPerAlbum; t++)
        {
            Track track = new()
            {
                Id = Guid.NewGuid(),
                Name = $"Track {a}-{t}",
                TrackNumber = t + 1,
                DiscNumber = 1,
                FolderId = folder.Id,
                LibraryFolder = folder,
            };
            tracks.Add(track);
        }
        context.Tracks.AddRange(tracks);
        context.SaveChanges();

        int trackIndex = 0;
        for (int a = 0; a < albumCount; a++)
        for (int t = 0; t < tracksPerAlbum; t++)
        {
            Track track = tracks[trackIndex++];
            context.AlbumTrack.Add(new(albums[a].Id, track.Id));
            context.ArtistTrack.Add(new(artist.Id, track.Id));
        }
        context.SaveChanges();

        interceptor.Clear();
        MusicRepository repository = new(factory);
        List<ArtistTrack> result = await repository.GetArtistTracksAsync(
            SeedConstants.UserId,
            artist.Id
        );

        Assert.Equal(albumCount * tracksPerAlbum, result.Count);

        int translationsQueryCount = interceptor.CapturedSql.Count(sql =>
            sql.Contains("\"Translations\"", StringComparison.Ordinal)
        );

        Assert.True(
            translationsQueryCount == 1,
            $"Expected exactly one query touching Translations for {albumCount} distinct albums, got {translationsQueryCount}"
        );

        connection.Dispose();
    }

    /// <summary>
    /// Correctness companion to the query-count regression above: since an artist's tracks
    /// span MULTIPLE distinct albums (unlike the single-album case), every track must be
    /// attached to its OWN album's Translation, not any other album's — proving the
    /// dictionary-keyed-by-AlbumId attach doesn't cross-wire tracks from different albums.
    /// </summary>
    [Fact]
    public async Task GetArtistTracksAsync_AttachesCorrectAlbumTranslations_AcrossMultipleDistinctAlbums()
    {
        (Library library, Folder folder) = SeedLibraryAndFolder(_context, SeedConstants.UserId);

        Artist artist = new()
        {
            Id = Guid.NewGuid(),
            Name = "Multi-Album Artist",
            LibraryId = library.Id,
            FolderId = folder.Id,
            HostFolder = "/media/music/Multi-Album Artist",
            Library = library,
            LibraryFolder = folder,
        };
        _context.Artists.Add(artist);

        Album albumOne = new()
        {
            Id = Guid.NewGuid(),
            Name = "First Album",
            LibraryId = library.Id,
            FolderId = folder.Id,
            HostFolder = "/media/music/Multi-Album Artist/First Album",
            Library = library,
            LibraryFolder = folder,
        };
        Album albumTwo = new()
        {
            Id = Guid.NewGuid(),
            Name = "Second Album",
            LibraryId = library.Id,
            FolderId = folder.Id,
            HostFolder = "/media/music/Multi-Album Artist/Second Album",
            Library = library,
            LibraryFolder = folder,
        };
        _context.Albums.AddRange([albumOne, albumTwo]);
        _context.SaveChanges();

        _context.Translations.Add(
            new()
            {
                AlbumId = albumOne.Id,
                Iso31661 = "US",
                Description = "First album description",
            }
        );
        _context.Translations.Add(
            new()
            {
                AlbumId = albumTwo.Id,
                Iso31661 = "US",
                Description = "Second album description",
            }
        );

        Track trackOnAlbumOne = new()
        {
            Id = Guid.NewGuid(),
            Name = "Track On First Album",
            TrackNumber = 1,
            DiscNumber = 1,
            FolderId = folder.Id,
            LibraryFolder = folder,
        };
        Track trackOnAlbumTwo = new()
        {
            Id = Guid.NewGuid(),
            Name = "Track On Second Album",
            TrackNumber = 1,
            DiscNumber = 1,
            FolderId = folder.Id,
            LibraryFolder = folder,
        };
        _context.Tracks.AddRange([trackOnAlbumOne, trackOnAlbumTwo]);
        _context.SaveChanges();

        _context.AlbumTrack.AddRange([
            new AlbumTrack(albumOne.Id, trackOnAlbumOne.Id),
            new AlbumTrack(albumTwo.Id, trackOnAlbumTwo.Id),
        ]);
        _context.ArtistTrack.AddRange([
            new ArtistTrack(artist.Id, trackOnAlbumOne.Id),
            new ArtistTrack(artist.Id, trackOnAlbumTwo.Id),
        ]);
        _context.SaveChanges();

        List<ArtistTrack> result = await _repository.GetArtistTracksAsync(
            SeedConstants.UserId,
            artist.Id
        );

        Assert.Equal(2, result.Count);

        ArtistTrack? onAlbumOne = result.FirstOrDefault(at => at.TrackId == trackOnAlbumOne.Id);
        ArtistTrack? onAlbumTwo = result.FirstOrDefault(at => at.TrackId == trackOnAlbumTwo.Id);
        Assert.NotNull(onAlbumOne);
        Assert.NotNull(onAlbumTwo);

        Assert.Equal(
            "First album description",
            onAlbumOne!.Track.AlbumTrack.First().Album.Translations.First().Description
        );
        Assert.Equal(
            "Second album description",
            onAlbumTwo!.Track.AlbumTrack.First().Album.Translations.First().Description
        );
    }

    #endregion

    #region Bug 3 — album playback cyclic Include + performance

    [Fact]
    public async Task GetAlbumTracksAsync_ReturnsFullyHydratedList_WithoutCyclicIncludeCrash()
    {
        (Library library, Folder folder) = SeedLibraryAndFolder(_context, SeedConstants.UserId);

        Artist artist = new()
        {
            Id = Guid.NewGuid(),
            Name = "Radiohead",
            LibraryId = library.Id,
            FolderId = folder.Id,
            HostFolder = "/media/music/Radiohead",
            Library = library,
            LibraryFolder = folder,
        };
        _context.Artists.Add(artist);

        Album album = new()
        {
            Id = Guid.NewGuid(),
            Name = "OK Computer",
            Cover = "/ok-computer.jpg",
            LibraryId = library.Id,
            FolderId = folder.Id,
            HostFolder = "/media/music/Radiohead/OK Computer",
            Year = 1997,
            Library = library,
            LibraryFolder = folder,
        };
        _context.Albums.Add(album);

        Track trackA = new()
        {
            Id = Guid.NewGuid(),
            Name = "Airbag",
            TrackNumber = 1,
            DiscNumber = 1,
            FolderId = folder.Id,
            LibraryFolder = folder,
        };
        Track trackB = new()
        {
            Id = Guid.NewGuid(),
            Name = "Paranoid Android",
            TrackNumber = 2,
            DiscNumber = 1,
            FolderId = folder.Id,
            LibraryFolder = folder,
        };
        _context.Tracks.AddRange([trackA, trackB]);
        _context.SaveChanges();

        _context.AlbumTrack.AddRange([
            new AlbumTrack(album.Id, trackA.Id),
            new AlbumTrack(album.Id, trackB.Id),
        ]);
        _context.ArtistTrack.AddRange([
            new ArtistTrack(artist.Id, trackA.Id),
            new ArtistTrack(artist.Id, trackB.Id),
        ]);
        _context.TrackUser.Add(new() { TrackId = trackB.Id, UserId = SeedConstants.UserId });
        _context.SaveChanges();

        List<AlbumTrack> result = await _repository.GetAlbumTracksAsync(
            SeedConstants.UserId,
            album.Id
        );

        Assert.Equal(2, result.Count);

        AlbumTrack? target = result.FirstOrDefault(at => at.TrackId == trackB.Id);
        Assert.NotNull(target);
        Assert.Equal("Paranoid Android", target!.Track.Name);
        Assert.Equal("Radiohead", target.Track.ArtistTrack.First().Artist.Name);
        // Proves the second-query attach populated Track.AlbumTrack (which album album this
        // track is on), not just the flat first-query columns.
        Assert.Equal("OK Computer", target.Track.AlbumTrack.First().Album.Name);
        Assert.NotEmpty(target.Track.TrackUser);
    }

    /// <summary>
    /// Reproduces the exact shape of the pre-fix query directly against this harness's real
    /// SQLite + real EF Core: rooted at AlbumTrack, revisiting AlbumTrack via Album.AlbumTrack.
    /// The original call site dodged this by running tracked (no AsNoTracking); this proves the
    /// shape is a genuine, unconditional cycle the moment AsNoTracking is added, matching the
    /// playlist/artist bugs above.
    /// </summary>
    [Fact]
    public async Task OldCyclicAlbumIncludeShape_StillThrowsInvalidOperationException()
    {
        (Library library, Folder folder) = SeedLibraryAndFolder(_context, SeedConstants.UserId);

        Album album = new()
        {
            Id = Guid.NewGuid(),
            Name = "Album",
            LibraryId = library.Id,
            FolderId = folder.Id,
            HostFolder = "/media/music/Artist/Album",
            Library = library,
            LibraryFolder = folder,
        };
        _context.Albums.Add(album);

        Track track = new()
        {
            Id = Guid.NewGuid(),
            Name = "Track",
            TrackNumber = 1,
            DiscNumber = 1,
            FolderId = folder.Id,
            LibraryFolder = folder,
        };
        _context.Tracks.Add(track);
        _context.SaveChanges();

        _context.AlbumTrack.Add(new(album.Id, track.Id));
        _context.SaveChanges();

        await using MediaContext queryContext = await _factory.CreateDbContextAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            queryContext
                .AlbumTrack.AsNoTracking()
                .Where(at => at.AlbumId == album.Id && at.TrackId == track.Id)
                .Include(at => at.Album)
                    .ThenInclude(a => a.AlbumTrack)
                        .ThenInclude(albumTrack => albumTrack.Track)
                .FirstOrDefaultAsync()
        );
    }

    [Fact]
    public async Task GetAlbumTracksAsync_CompletesWellUnderTwoSeconds_For150PlusTracks()
    {
        (Library library, Folder folder) = SeedLibraryAndFolder(_context, SeedConstants.UserId);

        Artist artist = new()
        {
            Id = Guid.NewGuid(),
            Name = "Various Artists",
            LibraryId = library.Id,
            FolderId = folder.Id,
            HostFolder = "/media/music/Various Artists",
            Library = library,
            LibraryFolder = folder,
        };
        _context.Artists.Add(artist);

        Album album = new()
        {
            Id = Guid.NewGuid(),
            Name = "The Box Set",
            LibraryId = library.Id,
            FolderId = folder.Id,
            HostFolder = "/media/music/Various Artists/The Box Set",
            Year = 2001,
            Library = library,
            LibraryFolder = folder,
        };
        _context.Albums.Add(album);
        _context.SaveChanges();

        const int discCount = 5;
        const int tracksPerDisc = 31; // 155 tracks total, matching the artist-bug scale
        List<Track> tracks = [];
        for (int d = 0; d < discCount; d++)
        for (int t = 0; t < tracksPerDisc; t++)
        {
            Track track = new()
            {
                Id = Guid.NewGuid(),
                Name = $"Track {d}-{t}",
                TrackNumber = t + 1,
                DiscNumber = d + 1,
                FolderId = folder.Id,
                LibraryFolder = folder,
            };
            tracks.Add(track);
        }
        _context.Tracks.AddRange(tracks);
        _context.SaveChanges();

        foreach (Track track in tracks)
        {
            _context.AlbumTrack.Add(new(album.Id, track.Id));
            _context.ArtistTrack.Add(new(artist.Id, track.Id));
        }
        _context.SaveChanges();

        Assert.Equal(discCount * tracksPerDisc, tracks.Count);

        Stopwatch stopwatch = Stopwatch.StartNew();
        List<AlbumTrack> result = await _repository.GetAlbumTracksAsync(
            SeedConstants.UserId,
            album.Id
        );
        stopwatch.Stop();
        _output.WriteLine(
            $"GetAlbumTracksAsync elapsed {stopwatch.ElapsedMilliseconds}ms for {result.Count} tracks"
        );

        Assert.Equal(discCount * tracksPerDisc, result.Count);
        Assert.All(result, at => Assert.NotEmpty(at.Track.ArtistTrack));

        Assert.True(
            stopwatch.ElapsedMilliseconds < 2000,
            $"GetAlbumTracksAsync took {stopwatch.ElapsedMilliseconds}ms for {result.Count} tracks — expected < 2000ms"
        );
    }

    /// <summary>
    /// Regression for the live-incident follow-up: GetAlbumTracksAsync's second query used to
    /// join Album.Images/Album.Translations by rooting through EVERY track (Track -&gt;
    /// AlbumTrack -&gt; Album), re-deriving the same album's Images/Translations once PER TRACK
    /// even though every track on an album shares it — measured 15-19 real seconds against the
    /// live dev DB for a 38-track album whose Translations table carries 500k+ rows across
    /// every entity type (GetPlaylist(album) took 15199ms live). The fix roots the
    /// Images/Translations fetch at Album directly (WHERE Id IN distinctAlbumIds), so the
    /// query count stays flat regardless of how many tracks share the album.
    /// </summary>
    [Fact]
    public async Task GetAlbumTracksAsync_QueriesAlbumImagesAndTranslations_OncePerDistinctAlbum_NotOncePerTrack()
    {
        SqlCaptureInterceptor interceptor = new();
        (IDbContextFactory<MediaContext> factory, SqliteConnection connection) =
            TestMediaContextFactory.CreateFactoryWithInterceptor(interceptor);
        await using MediaContext context = factory.CreateDbContext();

        (Library library, Folder folder) = SeedLibraryAndFolder(context, SeedConstants.UserId);

        Album album = new()
        {
            Id = Guid.NewGuid(),
            Name = "Radio 538: Hitzone 44",
            LibraryId = library.Id,
            FolderId = folder.Id,
            HostFolder = "/media/music/Various Artists/Radio 538 Hitzone 44",
            Year = 2008,
            Library = library,
            LibraryFolder = folder,
        };
        context.Albums.Add(album);
        context.SaveChanges();

        context.Images.Add(
            new()
            {
                AlbumId = album.Id,
                Type = "cover",
                FilePath = "/hitzone44.jpg",
                AspectRatio = 1.0,
            }
        );
        context.Translations.Add(
            new()
            {
                AlbumId = album.Id,
                Iso31661 = "NL",
                Description = "Compilatiealbum",
            }
        );
        context.Translations.Add(
            new()
            {
                AlbumId = album.Id,
                Iso31661 = "US",
                Description = "Compilation album",
            }
        );

        const int trackCount = 38; // matches the live incident's 37-38 track album
        List<Track> tracks = [];
        for (int t = 0; t < trackCount; t++)
        {
            Track track = new()
            {
                Id = Guid.NewGuid(),
                Name = $"Track {t}",
                TrackNumber = t + 1,
                DiscNumber = 1,
                FolderId = folder.Id,
                LibraryFolder = folder,
            };
            tracks.Add(track);
        }
        context.Tracks.AddRange(tracks);
        context.SaveChanges();

        foreach (Track track in tracks)
            context.AlbumTrack.Add(new(album.Id, track.Id));
        context.SaveChanges();

        interceptor.Clear();
        MusicRepository repository = new(factory);
        List<AlbumTrack> result = await repository.GetAlbumTracksAsync(
            SeedConstants.UserId,
            album.Id
        );

        Assert.Equal(trackCount, result.Count);

        int translationsQueryCount = interceptor.CapturedSql.Count(sql =>
            sql.Contains("\"Translations\"", StringComparison.Ordinal)
        );
        int imagesQueryCount = interceptor.CapturedSql.Count(sql =>
            sql.Contains("\"Images\"", StringComparison.Ordinal)
        );

        Assert.True(
            translationsQueryCount == 1,
            $"Expected exactly one query touching Translations for {trackCount} tracks sharing one album, got {translationsQueryCount}"
        );
        Assert.True(
            imagesQueryCount == 1,
            $"Expected exactly one query touching Images for {trackCount} tracks sharing one album, got {imagesQueryCount}"
        );

        connection.Dispose();
    }

    /// <summary>
    /// Correctness companion to the query-count regression above: every track's attached Album
    /// must still carry the full Images/Translations data, proving the "query once, attach to
    /// everyone in memory" restructuring doesn't drop data for tracks beyond the first.
    /// </summary>
    [Fact]
    public async Task GetAlbumTracksAsync_AttachesAlbumImagesAndTranslations_ToEveryTrack_WhenTracksShareOneAlbum()
    {
        (Library library, Folder folder) = SeedLibraryAndFolder(_context, SeedConstants.UserId);

        Album album = new()
        {
            Id = Guid.NewGuid(),
            Name = "Shared Album",
            LibraryId = library.Id,
            FolderId = folder.Id,
            HostFolder = "/media/music/Various Artists/Shared Album",
            Year = 2010,
            Library = library,
            LibraryFolder = folder,
        };
        _context.Albums.Add(album);
        _context.SaveChanges();

        _context.Images.Add(
            new()
            {
                AlbumId = album.Id,
                Type = "cover",
                FilePath = "/shared-album.jpg",
                AspectRatio = 1.0,
            }
        );
        _context.Translations.Add(
            new()
            {
                AlbumId = album.Id,
                Iso31661 = "US",
                Description = "A shared album",
            }
        );

        Track trackA = new()
        {
            Id = Guid.NewGuid(),
            Name = "Track A",
            TrackNumber = 1,
            DiscNumber = 1,
            FolderId = folder.Id,
            LibraryFolder = folder,
        };
        Track trackB = new()
        {
            Id = Guid.NewGuid(),
            Name = "Track B",
            TrackNumber = 2,
            DiscNumber = 1,
            FolderId = folder.Id,
            LibraryFolder = folder,
        };
        Track trackC = new()
        {
            Id = Guid.NewGuid(),
            Name = "Track C",
            TrackNumber = 3,
            DiscNumber = 1,
            FolderId = folder.Id,
            LibraryFolder = folder,
        };
        _context.Tracks.AddRange([trackA, trackB, trackC]);
        _context.SaveChanges();

        _context.AlbumTrack.AddRange([
            new AlbumTrack(album.Id, trackA.Id),
            new AlbumTrack(album.Id, trackB.Id),
            new AlbumTrack(album.Id, trackC.Id),
        ]);
        _context.SaveChanges();

        List<AlbumTrack> result = await _repository.GetAlbumTracksAsync(
            SeedConstants.UserId,
            album.Id
        );

        Assert.Equal(3, result.Count);
        Assert.All(
            result,
            at =>
            {
                AlbumTrack link = at.Track.AlbumTrack.First();
                Assert.Equal("Shared Album", link.Album.Name);
                Assert.NotEmpty(link.Album.Images);
                Assert.NotEmpty(link.Album.Translations);
                Assert.Equal("A shared album", link.Album.Translations.First().Description);
            }
        );
    }

    #endregion

    #region Bug 4 — genre playback cyclic Include + performance

    [Fact]
    public async Task GetGenreTracksAsync_ReturnsFullyHydratedList_WithoutCyclicIncludeCrash()
    {
        (Library library, Folder folder) = SeedLibraryAndFolder(_context, SeedConstants.UserId);

        Artist artist = new()
        {
            Id = Guid.NewGuid(),
            Name = "Boards of Canada",
            LibraryId = library.Id,
            FolderId = folder.Id,
            HostFolder = "/media/music/Boards of Canada",
            Library = library,
            LibraryFolder = folder,
        };
        _context.Artists.Add(artist);

        Album album = new()
        {
            Id = Guid.NewGuid(),
            Name = "Music Has the Right to Children",
            LibraryId = library.Id,
            FolderId = folder.Id,
            HostFolder = "/media/music/Boards of Canada/Music Has the Right to Children",
            Year = 1998,
            Library = library,
            LibraryFolder = folder,
        };
        _context.Albums.Add(album);

        MusicGenre genre = new() { Id = Guid.NewGuid(), Name = "IDM" };
        _context.MusicGenres.Add(genre);
        _context.SaveChanges();

        _context.AlbumMusicGenre.Add(new(album.Id, genre.Id));

        Track trackA = new()
        {
            Id = Guid.NewGuid(),
            Name = "Wildlife Analysis",
            TrackNumber = 1,
            DiscNumber = 1,
            FolderId = folder.Id,
            LibraryFolder = folder,
        };
        Track trackB = new()
        {
            Id = Guid.NewGuid(),
            Name = "An Eagle in Your Mind",
            TrackNumber = 2,
            DiscNumber = 1,
            FolderId = folder.Id,
            LibraryFolder = folder,
        };
        _context.Tracks.AddRange([trackA, trackB]);
        _context.SaveChanges();

        _context.AlbumTrack.AddRange([
            new AlbumTrack(album.Id, trackA.Id),
            new AlbumTrack(album.Id, trackB.Id),
        ]);
        _context.ArtistTrack.AddRange([
            new ArtistTrack(artist.Id, trackA.Id),
            new ArtistTrack(artist.Id, trackB.Id),
        ]);
        _context.MusicGenreTrack.AddRange([new(genre.Id, trackA.Id), new(genre.Id, trackB.Id)]);
        _context.TrackUser.Add(new() { TrackId = trackB.Id, UserId = SeedConstants.UserId });
        _context.SaveChanges();

        List<MusicGenreTrack> result = await _repository.GetGenreTracksAsync(
            SeedConstants.UserId,
            genre.Id
        );

        Assert.Equal(2, result.Count);

        MusicGenreTrack? target = result.FirstOrDefault(mgt => mgt.TrackId == trackB.Id);
        Assert.NotNull(target);
        Assert.Equal("An Eagle in Your Mind", target!.Track.Name);
        Assert.Equal("Boards of Canada", target.Track.ArtistTrack.First().Artist.Name);
        Assert.Equal("Music Has the Right to Children", target.Track.AlbumTrack.First().Album.Name);
        Assert.NotEmpty(target.Track.TrackUser);
    }

    [Fact]
    public async Task GetGenreTracksAsync_ReturnsEmpty_WhenGenreNotOwnedByCaller()
    {
        (Library library, Folder folder) = SeedLibraryAndFolder(_context, SeedConstants.UserId);
        _context.Users.Add(
            new()
            {
                Id = OtherUserId,
                Email = "other@nomercy.tv",
                Name = "Other User",
                Owner = false,
                Allowed = true,
                Manage = false,
            }
        );

        // A second library owned only by OtherUserId, so SeedConstants.UserId has no access.
        Library otherLibrary = new()
        {
            Id = Ulid.NewUlid(),
            Title = "Other Music",
            Type = "music",
            Order = 4,
        };
        _context.Libraries.Add(otherLibrary);

        Folder otherFolder = new()
        {
            Id = Ulid.NewUlid(),
            Path = "/media/other-music",
            DriverId = Driver.SystemLocalDriverId,
        };
        _context.Folders.Add(otherFolder);
        _context.SaveChanges();

        _context.LibraryUser.Add(new(otherLibrary.Id, OtherUserId));
        _context.FolderLibrary.Add(new(otherFolder.Id, otherLibrary.Id));
        _context.SaveChanges();

        Album album = new()
        {
            Id = Guid.NewGuid(),
            Name = "Private Album",
            LibraryId = otherLibrary.Id,
            FolderId = otherFolder.Id,
            HostFolder = "/media/other-music/Private Album",
            Library = otherLibrary,
            LibraryFolder = otherFolder,
        };
        _context.Albums.Add(album);

        MusicGenre genre = new() { Id = Guid.NewGuid(), Name = "Private Genre" };
        _context.MusicGenres.Add(genre);
        _context.SaveChanges();

        _context.AlbumMusicGenre.Add(new(album.Id, genre.Id));

        Track track = new()
        {
            Id = Guid.NewGuid(),
            Name = "Private Track",
            TrackNumber = 1,
            DiscNumber = 1,
            FolderId = folder.Id,
            LibraryFolder = folder,
        };
        _context.Tracks.Add(track);
        _context.SaveChanges();

        _context.AlbumTrack.Add(new(album.Id, track.Id));
        _context.MusicGenreTrack.Add(new(genre.Id, track.Id));
        _context.SaveChanges();

        // Requesting as SeedConstants.UserId, who has no access to otherLibrary.
        List<MusicGenreTrack> result = await _repository.GetGenreTracksAsync(
            SeedConstants.UserId,
            genre.Id
        );

        Assert.Empty(result);
    }

    /// <summary>
    /// Reproduces the exact shape of the pre-fix query directly against this harness's real
    /// SQLite + real EF Core: rooted at MusicGenreTrack, revisiting MusicGenreTrack via
    /// Genre.MusicGenreTracks. The original call site dodged this by running tracked (no
    /// AsNoTracking); this proves the shape is a genuine, unconditional cycle the moment
    /// AsNoTracking is added, matching the playlist/artist/album bugs above.
    /// </summary>
    [Fact]
    public async Task OldCyclicGenreIncludeShape_StillThrowsInvalidOperationException()
    {
        (Library library, Folder folder) = SeedLibraryAndFolder(_context, SeedConstants.UserId);

        Album album = new()
        {
            Id = Guid.NewGuid(),
            Name = "Album",
            LibraryId = library.Id,
            FolderId = folder.Id,
            HostFolder = "/media/music/Artist/Album",
            Library = library,
            LibraryFolder = folder,
        };
        _context.Albums.Add(album);

        MusicGenre genre = new() { Id = Guid.NewGuid(), Name = "Genre" };
        _context.MusicGenres.Add(genre);
        _context.SaveChanges();

        _context.AlbumMusicGenre.Add(new(album.Id, genre.Id));

        Track track = new()
        {
            Id = Guid.NewGuid(),
            Name = "Track",
            TrackNumber = 1,
            DiscNumber = 1,
            FolderId = folder.Id,
            LibraryFolder = folder,
        };
        _context.Tracks.Add(track);
        _context.SaveChanges();

        _context.MusicGenreTrack.Add(new(genre.Id, track.Id));
        _context.SaveChanges();

        await using MediaContext queryContext = await _factory.CreateDbContextAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            queryContext
                .MusicGenreTrack.AsNoTracking()
                .Where(mgt => mgt.GenreId == genre.Id && mgt.TrackId == track.Id)
                .Include(mgt => mgt.Genre)
                    .ThenInclude(g => g.MusicGenreTracks)
                        .ThenInclude(genreTrack => genreTrack.Track)
                .FirstOrDefaultAsync()
        );
    }

    [Fact]
    public async Task GetGenreTracksAsync_CompletesWellUnderTwoSeconds_For150PlusTracks()
    {
        (Library library, Folder folder) = SeedLibraryAndFolder(_context, SeedConstants.UserId);

        Artist artist = new()
        {
            Id = Guid.NewGuid(),
            Name = "Various Artists",
            LibraryId = library.Id,
            FolderId = folder.Id,
            HostFolder = "/media/music/Various Artists",
            Library = library,
            LibraryFolder = folder,
        };
        _context.Artists.Add(artist);

        Album album = new()
        {
            Id = Guid.NewGuid(),
            Name = "Genre Compilation",
            LibraryId = library.Id,
            FolderId = folder.Id,
            HostFolder = "/media/music/Various Artists/Genre Compilation",
            Year = 2005,
            Library = library,
            LibraryFolder = folder,
        };
        _context.Albums.Add(album);

        MusicGenre genre = new() { Id = Guid.NewGuid(), Name = "Electronic" };
        _context.MusicGenres.Add(genre);
        _context.SaveChanges();

        _context.AlbumMusicGenre.Add(new(album.Id, genre.Id));
        _context.SaveChanges();

        const int discCount = 5;
        const int tracksPerDisc = 31; // 155 tracks total, matching the artist-bug scale
        List<Track> tracks = [];
        for (int d = 0; d < discCount; d++)
        for (int t = 0; t < tracksPerDisc; t++)
        {
            Track track = new()
            {
                Id = Guid.NewGuid(),
                Name = $"Track {d}-{t}",
                TrackNumber = t + 1,
                DiscNumber = d + 1,
                FolderId = folder.Id,
                LibraryFolder = folder,
            };
            tracks.Add(track);
        }
        _context.Tracks.AddRange(tracks);
        _context.SaveChanges();

        foreach (Track track in tracks)
        {
            _context.AlbumTrack.Add(new(album.Id, track.Id));
            _context.ArtistTrack.Add(new(artist.Id, track.Id));
            _context.MusicGenreTrack.Add(new(genre.Id, track.Id));
        }
        _context.SaveChanges();

        Assert.Equal(discCount * tracksPerDisc, tracks.Count);

        Stopwatch stopwatch = Stopwatch.StartNew();
        List<MusicGenreTrack> result = await _repository.GetGenreTracksAsync(
            SeedConstants.UserId,
            genre.Id
        );
        stopwatch.Stop();
        _output.WriteLine(
            $"GetGenreTracksAsync elapsed {stopwatch.ElapsedMilliseconds}ms for {result.Count} tracks"
        );

        Assert.Equal(discCount * tracksPerDisc, result.Count);
        Assert.All(result, mgt => Assert.NotEmpty(mgt.Track.ArtistTrack));

        Assert.True(
            stopwatch.ElapsedMilliseconds < 2000,
            $"GetGenreTracksAsync took {stopwatch.ElapsedMilliseconds}ms for {result.Count} tracks — expected < 2000ms"
        );
    }

    /// <summary>
    /// Regression for the live-incident follow-up: GetGenreTracksAsync used to join
    /// Album.AlbumArtist.Artist.Images and Album.Translations by rooting through EVERY track
    /// (Track -&gt; AlbumTrack -&gt; Album), re-deriving each distinct album's data once PER
    /// TRACK on that album instead of once per distinct album — the identical bug shape
    /// fixed for GetAlbumTracksAsync/GetArtistTracksAsync. The fix roots that fetch at Album
    /// directly (WHERE Id IN distinctAlbumIds), so the query count stays flat regardless of
    /// how many tracks — or how many distinct albums — the genre spans.
    /// </summary>
    [Fact]
    public async Task GetGenreTracksAsync_QueriesAlbumImagesAndTranslations_OncePerDistinctAlbum_NotOncePerTrack()
    {
        SqlCaptureInterceptor interceptor = new();
        (IDbContextFactory<MediaContext> factory, SqliteConnection connection) =
            TestMediaContextFactory.CreateFactoryWithInterceptor(interceptor);
        await using MediaContext context = factory.CreateDbContext();

        (Library library, Folder folder) = SeedLibraryAndFolder(context, SeedConstants.UserId);

        Artist artist = new()
        {
            Id = Guid.NewGuid(),
            Name = "Various Artists",
            LibraryId = library.Id,
            FolderId = folder.Id,
            HostFolder = "/media/music/Various Artists",
            Library = library,
            LibraryFolder = folder,
        };
        context.Artists.Add(artist);
        context.SaveChanges();

        context.Images.Add(
            new()
            {
                ArtistId = artist.Id,
                Type = "background",
                FilePath = "/various-artists-bg.jpg",
                AspectRatio = 1.78,
            }
        );

        MusicGenre genre = new() { Id = Guid.NewGuid(), Name = "Electronic" };
        context.MusicGenres.Add(genre);
        context.SaveChanges();

        const int albumCount = 5;
        const int tracksPerAlbum = 31; // 155 tracks total, matching the artist-bug scale
        List<Album> albums = [];
        for (int a = 0; a < albumCount; a++)
        {
            Album album = new()
            {
                Id = Guid.NewGuid(),
                Name = $"Compilation {a}",
                LibraryId = library.Id,
                FolderId = folder.Id,
                HostFolder = $"/media/music/Various Artists/Compilation {a}",
                Year = 2000 + a,
                Library = library,
                LibraryFolder = folder,
            };
            albums.Add(album);
        }
        context.Albums.AddRange(albums);
        context.SaveChanges();

        foreach (Album album in albums)
        {
            context.AlbumMusicGenre.Add(new(album.Id, genre.Id));
            context.AlbumArtist.Add(new(album.Id, artist.Id));
            context.Translations.Add(
                new()
                {
                    AlbumId = album.Id,
                    Iso31661 = "US",
                    Description = $"{album.Name} description",
                }
            );
        }
        context.SaveChanges();

        List<Track> tracks = [];
        for (int a = 0; a < albumCount; a++)
        for (int t = 0; t < tracksPerAlbum; t++)
        {
            Track track = new()
            {
                Id = Guid.NewGuid(),
                Name = $"Track {a}-{t}",
                TrackNumber = t + 1,
                DiscNumber = 1,
                FolderId = folder.Id,
                LibraryFolder = folder,
            };
            tracks.Add(track);
        }
        context.Tracks.AddRange(tracks);
        context.SaveChanges();

        int trackIndex = 0;
        for (int a = 0; a < albumCount; a++)
        for (int t = 0; t < tracksPerAlbum; t++)
        {
            Track track = tracks[trackIndex++];
            context.AlbumTrack.Add(new(albums[a].Id, track.Id));
            context.MusicGenreTrack.Add(new(genre.Id, track.Id));
        }
        context.SaveChanges();

        interceptor.Clear();
        MusicRepository repository = new(factory);
        List<MusicGenreTrack> result = await repository.GetGenreTracksAsync(
            SeedConstants.UserId,
            genre.Id
        );

        Assert.Equal(albumCount * tracksPerAlbum, result.Count);

        int translationsQueryCount = interceptor.CapturedSql.Count(sql =>
            sql.Contains("\"Translations\"", StringComparison.Ordinal)
        );
        int imagesQueryCount = interceptor.CapturedSql.Count(sql =>
            sql.Contains("\"Images\"", StringComparison.Ordinal)
        );

        Assert.True(
            translationsQueryCount == 1,
            $"Expected exactly one query touching Translations for {albumCount} distinct albums, got {translationsQueryCount}"
        );
        Assert.True(
            imagesQueryCount == 1,
            $"Expected exactly one query touching Images for {albumCount} distinct albums, got {imagesQueryCount}"
        );

        connection.Dispose();
    }

    /// <summary>
    /// Correctness companion to the query-count regression above: every track must keep the
    /// right album's Translations/Images even though the genre spans multiple distinct
    /// albums — proving the dictionary-keyed-by-AlbumId attach doesn't cross-wire tracks
    /// from different albums.
    /// </summary>
    [Fact]
    public async Task GetGenreTracksAsync_AttachesCorrectAlbumData_AcrossMultipleDistinctAlbums()
    {
        (Library library, Folder folder) = SeedLibraryAndFolder(_context, SeedConstants.UserId);

        MusicGenre genre = new() { Id = Guid.NewGuid(), Name = "House" };
        _context.MusicGenres.Add(genre);

        Album albumOne = new()
        {
            Id = Guid.NewGuid(),
            Name = "First Compilation",
            LibraryId = library.Id,
            FolderId = folder.Id,
            HostFolder = "/media/music/Various Artists/First Compilation",
            Library = library,
            LibraryFolder = folder,
        };
        Album albumTwo = new()
        {
            Id = Guid.NewGuid(),
            Name = "Second Compilation",
            LibraryId = library.Id,
            FolderId = folder.Id,
            HostFolder = "/media/music/Various Artists/Second Compilation",
            Library = library,
            LibraryFolder = folder,
        };
        _context.Albums.AddRange([albumOne, albumTwo]);
        _context.SaveChanges();

        _context.AlbumMusicGenre.AddRange([new(albumOne.Id, genre.Id), new(albumTwo.Id, genre.Id)]);
        _context.Translations.Add(
            new()
            {
                AlbumId = albumOne.Id,
                Iso31661 = "US",
                Description = "First compilation description",
            }
        );
        _context.Translations.Add(
            new()
            {
                AlbumId = albumTwo.Id,
                Iso31661 = "US",
                Description = "Second compilation description",
            }
        );

        Track trackOnAlbumOne = new()
        {
            Id = Guid.NewGuid(),
            Name = "Track On First Compilation",
            TrackNumber = 1,
            DiscNumber = 1,
            FolderId = folder.Id,
            LibraryFolder = folder,
        };
        Track trackOnAlbumTwo = new()
        {
            Id = Guid.NewGuid(),
            Name = "Track On Second Compilation",
            TrackNumber = 1,
            DiscNumber = 1,
            FolderId = folder.Id,
            LibraryFolder = folder,
        };
        _context.Tracks.AddRange([trackOnAlbumOne, trackOnAlbumTwo]);
        _context.SaveChanges();

        _context.AlbumTrack.AddRange([
            new AlbumTrack(albumOne.Id, trackOnAlbumOne.Id),
            new AlbumTrack(albumTwo.Id, trackOnAlbumTwo.Id),
        ]);
        _context.MusicGenreTrack.AddRange([
            new(genre.Id, trackOnAlbumOne.Id),
            new(genre.Id, trackOnAlbumTwo.Id),
        ]);
        _context.SaveChanges();

        List<MusicGenreTrack> result = await _repository.GetGenreTracksAsync(
            SeedConstants.UserId,
            genre.Id
        );

        Assert.Equal(2, result.Count);

        MusicGenreTrack? onAlbumOne = result.FirstOrDefault(mgt =>
            mgt.TrackId == trackOnAlbumOne.Id
        );
        MusicGenreTrack? onAlbumTwo = result.FirstOrDefault(mgt =>
            mgt.TrackId == trackOnAlbumTwo.Id
        );
        Assert.NotNull(onAlbumOne);
        Assert.NotNull(onAlbumTwo);

        Assert.Equal(
            "First compilation description",
            onAlbumOne!.Track.AlbumTrack.First().Album.Translations.First().Description
        );
        Assert.Equal(
            "Second compilation description",
            onAlbumTwo!.Track.AlbumTrack.First().Album.Translations.First().Description
        );
    }

    #endregion

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }
}
