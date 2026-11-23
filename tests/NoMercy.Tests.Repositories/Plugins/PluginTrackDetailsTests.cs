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
using Moq;
using NoMercy.Data.Plugins;
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Music;
using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.Tests.Repositories.Plugins;

/// <summary>
/// What a plugin that groups, orders or picks tracks is told about one: its
/// genres, its year, the album it is on and everyone who plays on it.
/// </summary>
[Trait("Category", "Unit")]
public sealed class PluginTrackDetailsTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MediaContext> _options;
    private readonly Ulid _library = Ulid.NewUlid();

    private readonly Guid _duet = Guid.NewGuid();
    private readonly Guid _onTwoAlbums = Guid.NewGuid();
    private readonly Guid _bare = Guid.NewGuid();
    private readonly Guid _onAnUndatedAlbum = Guid.NewGuid();

    private readonly Guid _first = Guid.NewGuid();
    private readonly Guid _greatest = Guid.NewGuid();
    private readonly Guid _undated = Guid.NewGuid();

    private readonly Guid _zed = Guid.NewGuid();
    private readonly Guid _abba = Guid.NewGuid();

    public PluginTrackDetailsTests()
    {
        _connection = new("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<MediaContext>().UseSqlite(_connection).Options;

        using MediaContext context = new(_options);
        context.Database.EnsureCreated();

        using SqliteCommand relax = _connection.CreateCommand();
        relax.CommandText = "PRAGMA foreign_keys = OFF;";
        relax.ExecuteNonQuery();

        context.Libraries.Add(
            new Library
            {
                Id = _library,
                Title = "Music",
                Type = "music",
            }
        );
        context.Tracks.AddRange(
            new Track
            {
                Id = _duet,
                Name = "Duet",
                Date = new DateTime(2001, 5, 5),
            },
            new Track { Id = _onTwoAlbums, Name = "On two albums" },
            new Track { Id = _bare, Name = "Bare" },
            new Track { Id = _onAnUndatedAlbum, Name = "On an undated album" }
        );
        context.LibraryTrack.AddRange(
            new[] { _duet, _onTwoAlbums, _bare, _onAnUndatedAlbum }.Select(track => new LibraryTrack
            {
                LibraryId = _library,
                TrackId = track,
            })
        );

        context.Albums.AddRange(
            Album(_first, "First", 1999),
            Album(_greatest, "Greatest", 2005),
            Album(_undated, "Undated", 0)
        );
        context.AlbumTrack.AddRange(
            new AlbumTrack { AlbumId = _first, TrackId = _duet },
            new AlbumTrack { AlbumId = _first, TrackId = _onTwoAlbums },
            new AlbumTrack { AlbumId = _greatest, TrackId = _onTwoAlbums },
            new AlbumTrack { AlbumId = _undated, TrackId = _onAnUndatedAlbum }
        );

        context.Artists.AddRange(
            new Artist
            {
                Id = _zed,
                Name = "Zed",
                HostFolder = "/music/zed",
            },
            new Artist
            {
                Id = _abba,
                Name = "Abba",
                HostFolder = "/music/abba",
            }
        );
        context.ArtistTrack.AddRange(
            new ArtistTrack { ArtistId = _zed, TrackId = _duet },
            new ArtistTrack { ArtistId = _abba, TrackId = _duet }
        );

        Guid rock = Guid.NewGuid();
        Guid pop = Guid.NewGuid();
        Guid disco = Guid.NewGuid();
        context.MusicGenres.AddRange(
            new MusicGenre { Id = rock, Name = "Rock" },
            new MusicGenre { Id = pop, Name = "Pop" },
            new MusicGenre { Id = disco, Name = "Disco" }
        );
        context.MusicGenreTrack.AddRange(
            new MusicGenreTrack { GenreId = rock, TrackId = _duet },
            new MusicGenreTrack { GenreId = pop, TrackId = _duet }
        );
        context.AlbumMusicGenre.AddRange(
            new AlbumMusicGenre { AlbumId = _first, MusicGenreId = disco },
            new AlbumMusicGenre { AlbumId = _greatest, MusicGenreId = disco },
            new AlbumMusicGenre { AlbumId = _greatest, MusicGenreId = pop }
        );

        context.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task A_track_with_two_artists_and_two_genres_shows_both_of_each()
    {
        PluginTrack track = await Read(_duet);

        track
            .Artists.Should()
            .BeEquivalentTo([new PluginTrackArtist(_abba, "Abba"), new(_zed, "Zed")]);
        track.Genres.Should().Equal("Pop", "Rock");
    }

    /// <summary>The track's own genres count. Its album's are for a track that has none.</summary>
    [Fact]
    public async Task A_track_with_genres_of_its_own_does_not_take_its_albums()
    {
        (await Read(_duet)).Genres.Should().NotContain("Disco");
    }

    [Fact]
    public async Task A_track_without_a_genre_of_its_own_shows_its_albums_each_once()
    {
        (await Read(_onTwoAlbums)).Genres.Should().Equal("Disco", "Pop");
    }

    [Fact]
    public async Task A_track_on_two_albums_lists_both()
    {
        PluginTrack track = await Read(_onTwoAlbums);

        track
            .Albums.Should()
            .BeEquivalentTo([new PluginTrackAlbum(_first, "First"), new(_greatest, "Greatest")]);
    }

    /// <summary>
    /// The single names a plugin already reads and the new lists never
    /// disagree about which album and which artist come first.
    /// </summary>
    [Fact]
    public async Task The_album_and_artist_shown_by_name_are_the_first_of_their_lists()
    {
        foreach (Guid id in new[] { _duet, _onTwoAlbums })
        {
            PluginTrack track = await Read(id);

            track.Albums[0].Name.Should().Be(track.Album);
            track.AlbumId.Should().Be(track.Albums[0].Id);
            if (track.Artist is not null)
                track.Artists[0].Name.Should().Be(track.Artist);
        }
    }

    [Fact]
    public async Task The_year_is_the_tracks_own_when_it_has_a_date()
    {
        (await Read(_duet)).Year.Should().Be(2001);
    }

    [Fact]
    public async Task Without_a_date_the_year_is_that_of_the_album_shown()
    {
        PluginTrack track = await Read(_onTwoAlbums);

        track.Year.Should().Be(track.Album == "First" ? 1999 : 2005);
    }

    [Fact]
    public async Task An_album_without_a_year_gives_none()
    {
        PluginTrack track = await Read(_onAnUndatedAlbum);

        track.Year.Should().BeNull();
        track.AlbumId.Should().Be(_undated);
    }

    [Fact]
    public async Task A_track_on_no_album_by_no_artist_has_empty_lists_and_no_year()
    {
        PluginTrack track = await Read(_bare);

        track.Genres.Should().BeEmpty();
        track.Albums.Should().BeEmpty();
        track.Artists.Should().BeEmpty();
        track.AlbumId.Should().BeNull();
        track.Year.Should().BeNull();
        track.Album.Should().BeNull();
        track.Artist.Should().BeNull();
    }

    /// <summary>A page of the library and a read by id run the same projection.</summary>
    [Fact]
    public async Task A_page_shows_every_track_as_a_read_by_its_id_does()
    {
        IReadOnlyList<PluginTrack> page = await Query().GetTracksAsync(_library.ToString());

        page.Should().HaveCount(4);
        foreach (PluginTrack listed in page)
            listed.Should().Be(await Read(listed.Id));
    }

    private async Task<PluginTrack> Read(Guid trackId) =>
        (await Query().GetTracksByIdAsync([trackId])).Should().ContainSingle().Subject;

    private PluginMusicQuery Query()
    {
        Mock<IDbContextFactory<MediaContext>> factory = new();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new(_options));

        return new(factory.Object);
    }

    private Album Album(Guid id, string name, int year) =>
        new()
        {
            Id = id,
            Name = name,
            Year = year,
            LibraryId = _library,
            Library = null!,
            LibraryFolder = null!,
        };
}
