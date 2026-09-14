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

using Moq;
using NoMercy.Api.DTOs.Media;
using NoMercy.Api.Services.Video;
using NoMercy.Data.Repositories;
using NoMercy.Database;
using NoMercy.Database.Models.Media;
using NoMercy.Database.Models.Movies;
using NoMercy.Database.Models.TvShows;
using NoMercy.Database.Models.Users;
using NoMercy.NmSystem.Domain;
using Xunit;

namespace NoMercy.Tests.Api.Services.Video;

// VideoPlaylistManager is the orchestration layer behind "start playing this
// item" for movies, tv, collections and specials -- it decides WHICH repository
// to call, WHICH item in the resulting list becomes "the thing to play now"
// (explicit itemId, else most-recently-progressed, else first), and it is the
// place that stamps the dynamic "playlist_id" onto every DTO in the list. None
// of that branching had a single test before this file.
[Trait("Category", "Playlist")]
public sealed class VideoPlaylistManagerTests
{
    private readonly Mock<IMovieRepository> _movieRepository = new();
    private readonly Mock<ITvShowRepository> _tvShowRepository = new();
    private readonly Mock<ICollectionRepository> _collectionRepository = new();
    private readonly Mock<ISpecialRepository> _specialRepository = new();

    private VideoPlaylistManager CreateManager()
    {
        return new(
            new MediaContext(),
            _movieRepository.Object,
            _collectionRepository.Object,
            _specialRepository.Object,
            _tvShowRepository.Object
        );
    }

    private static Movie BuildMovie(int id, UserData? userData = null)
    {
        Movie movie = new()
        {
            Id = id,
            Title = $"Movie {id}",
            TitleSort = $"movie {id}",
            Overview = "Overview",
            ReleaseDate = new DateTime(2020, 1, 1),
        };
        VideoFile file = new()
        {
            Filename = $"movie-{id}.mkv",
            Folder = "/movies",
            HostFolder = "/movies",
            Languages = "[\"en\"]",
            Quality = "1080p",
            Share = "movies",
            MovieId = id,
            Movie = movie,
        };
        if (userData is not null)
            file.UserData.Add(userData);
        movie.VideoFiles.Add(file);
        return movie;
    }

    private static Episode BuildEpisode(int id, Tv tv, Season season, UserData? userData = null)
    {
        Episode episode = new()
        {
            Id = id,
            Title = $"Episode {id}",
            EpisodeNumber = id,
            SeasonNumber = season.SeasonNumber,
            TvId = tv.Id,
            Tv = tv,
            SeasonId = season.Id,
            Season = season,
        };
        VideoFile file = new()
        {
            Filename = $"episode-{id}.mkv",
            Folder = "/tv",
            HostFolder = "/tv",
            Languages = "[\"en\"]",
            Quality = "1080p",
            Share = "tv",
            EpisodeId = id,
            Episode = episode,
        };
        if (userData is not null)
            file.UserData.Add(userData);
        episode.VideoFiles.Add(file);
        return episode;
    }

    private static UserData ProgressAt(DateTime playedAt)
    {
        return new()
        {
            Type = "progress",
            Time = 120,
            LastPlayedDate = playedAt.ToString("O"),
        };
    }

    [Fact]
    public async Task GetPlaylist_UnknownType_ThrowsArgumentException()
    {
        VideoPlaylistManager manager = CreateManager();

        Func<Task> act = async () =>
            await manager.GetPlaylist(Guid.NewGuid(), "not-a-real-type", "1", null, "en", "US");

        (await act.Should().ThrowAsync<ArgumentException>()).WithParameterName("type");
    }

    [Fact]
    public async Task GetPlaylist_MovieType_ParsesStringListId_AndStampsIntPlaylistId()
    {
        Movie movie = BuildMovie(42);
        _movieRepository
            .Setup(r => r.GetMoviePlaylistAsync(It.IsAny<Guid>(), 42, "en", "US", default))
            .ReturnsAsync([movie]);
        VideoPlaylistManager manager = CreateManager();

        (VideoPlaylistResponseDto? item, List<VideoPlaylistResponseDto> playlist) =
            await manager.GetPlaylist(Guid.NewGuid(), "movie", "42", null, "en", "US");

        playlist.Should().ContainSingle();
        item.Should().NotBeNull();
        item!.Id.Should().Be(42);
        // The manager re-parses the string listId to an int before handing it to
        // the DTO as the dynamic "playlist_id" -- movies serialize that field as
        // a JSON number, unlike tv/collection/special (see tests below).
        ((object)item.PlaylistId)
            .Should()
            .Be(42);
    }

    [Fact]
    public async Task GetPlaylist_MovieType_WithNonStringListId_ThrowsRuntimeBinderException()
    {
        // GetPlaylist's listId is `dynamic` precisely so SignalR/JSON callers can
        // hand it through untouched, but every branch immediately calls
        // int.Parse/Ulid.Parse(listId) against it. If a caller ever passes an
        // already-numeric dynamic (e.g. a raw int from a different transport),
        // dynamic overload resolution has no int.Parse(int) to bind to and blows
        // up at the call site instead of the type mismatch being caught early.
        // Locking this in so a future refactor of the parse call doesn't
        // silently start accepting ints while pretending strings still work.
        _movieRepository
            .Setup(r =>
                r.GetMoviePlaylistAsync(It.IsAny<Guid>(), It.IsAny<int>(), "en", "US", default)
            )
            .ReturnsAsync([]);
        VideoPlaylistManager manager = CreateManager();
        dynamic nonStringListId = 42;

        Func<Task> act = async () =>
            await manager.GetPlaylist(Guid.NewGuid(), "movie", nonStringListId, null, "en", "US");

        await act.Should().ThrowAsync<Microsoft.CSharp.RuntimeBinder.RuntimeBinderException>();
    }

    [Fact]
    public async Task GetPlaylist_TvType_PreservesOriginalListIdOnPlaylistId()
    {
        Tv tv = new()
        {
            Id = 1399,
            Title = "Breaking Bad",
            TitleSort = "breaking bad",
            FirstAirDate = new DateTime(2008, 1, 20),
        };
        Season season = new()
        {
            Id = 1,
            SeasonNumber = 1,
            TvId = tv.Id,
            Tv = tv,
        };
        tv.Seasons.Add(season);
        Episode episode = BuildEpisode(1, tv, season);
        season.Episodes.Add(episode);

        _tvShowRepository
            .Setup(r => r.GetPlaylistAsync(It.IsAny<Guid>(), 1399, "en", "US", default))
            .ReturnsAsync(tv);
        VideoPlaylistManager manager = CreateManager();

        (VideoPlaylistResponseDto? item, List<VideoPlaylistResponseDto> playlist) =
            await manager.GetPlaylist(Guid.NewGuid(), "tv", "1399", null, "en", "US");

        playlist.Should().ContainSingle();
        item.Should().NotBeNull();
        // Unlike movie, tv/collection/special hand the ORIGINAL dynamic value
        // straight through as playlist_id -- here that is still the string.
        ((object)item!.PlaylistId)
            .Should()
            .Be("1399");
    }

    [Fact]
    public async Task GetPlaylist_TvType_OrdersRegularSeasonsBeforeSpecialsSeason()
    {
        Tv tv = new()
        {
            Id = 5,
            Title = "Show",
            TitleSort = "show",
        };
        Season regular = new()
        {
            Id = 1,
            SeasonNumber = 1,
            TvId = tv.Id,
            Tv = tv,
        };
        Season extras = new()
        {
            Id = 2,
            SeasonNumber = 0,
            TvId = tv.Id,
            Tv = tv,
        };
        Episode regularEpisode = BuildEpisode(10, tv, regular);
        Episode extraEpisode = BuildEpisode(11, tv, extras);
        regular.Episodes.Add(regularEpisode);
        extras.Episodes.Add(extraEpisode);
        // Extras season deliberately added to Seasons BEFORE the regular season
        // to prove the split is by SeasonNumber, not by insertion order.
        tv.Seasons.Add(extras);
        tv.Seasons.Add(regular);

        _tvShowRepository
            .Setup(r => r.GetPlaylistAsync(It.IsAny<Guid>(), 5, "en", "US", default))
            .ReturnsAsync(tv);
        VideoPlaylistManager manager = CreateManager();

        (_, List<VideoPlaylistResponseDto> playlist) = await manager.GetPlaylist(
            Guid.NewGuid(),
            "tv",
            "5",
            null,
            "en",
            "US"
        );

        playlist.Select(p => p.Id).Should().Equal(10, 11);
    }

    [Fact]
    public async Task GetPlaylist_TvType_ItemIdMatch_SelectsThatEpisodeRegardlessOfProgress()
    {
        Tv tv = new()
        {
            Id = 7,
            Title = "Show",
            TitleSort = "show",
        };
        Season season = new()
        {
            Id = 1,
            SeasonNumber = 1,
            TvId = tv.Id,
            Tv = tv,
        };
        Episode first = BuildEpisode(20, tv, season, ProgressAt(new DateTime(2026, 1, 1)));
        Episode second = BuildEpisode(21, tv, season);
        season.Episodes.Add(first);
        season.Episodes.Add(second);
        tv.Seasons.Add(season);

        _tvShowRepository
            .Setup(r => r.GetPlaylistAsync(It.IsAny<Guid>(), 7, "en", "US", default))
            .ReturnsAsync(tv);
        VideoPlaylistManager manager = CreateManager();

        (VideoPlaylistResponseDto? item, _) = await manager.GetPlaylist(
            Guid.NewGuid(),
            "tv",
            "7",
            21,
            "en",
            "US"
        );

        item.Should().NotBeNull();
        item!.Id.Should().Be(21);
    }

    [Fact]
    public async Task GetPlaylist_TvType_NoItemIdMatch_FallsBackToMostRecentProgress()
    {
        Tv tv = new()
        {
            Id = 8,
            Title = "Show",
            TitleSort = "show",
        };
        Season season = new()
        {
            Id = 1,
            SeasonNumber = 1,
            TvId = tv.Id,
            Tv = tv,
        };
        Episode older = BuildEpisode(30, tv, season, ProgressAt(new DateTime(2025, 1, 1)));
        Episode newer = BuildEpisode(31, tv, season, ProgressAt(new DateTime(2026, 6, 1)));
        Episode noProgress = BuildEpisode(32, tv, season);
        season.Episodes.Add(older);
        season.Episodes.Add(newer);
        season.Episodes.Add(noProgress);
        tv.Seasons.Add(season);

        _tvShowRepository
            .Setup(r => r.GetPlaylistAsync(It.IsAny<Guid>(), 8, "en", "US", default))
            .ReturnsAsync(tv);
        VideoPlaylistManager manager = CreateManager();

        (VideoPlaylistResponseDto? item, _) = await manager.GetPlaylist(
            Guid.NewGuid(),
            "tv",
            "8",
            itemId: 999,
            "en",
            "US"
        );

        item.Should().NotBeNull();
        item!.Id.Should().Be(31, "the episode with the latest LastPlayedDate must win");
    }

    [Fact]
    public async Task GetPlaylist_TvType_NoItemIdMatchAndNoProgress_FallsBackToFirst()
    {
        Tv tv = new()
        {
            Id = 9,
            Title = "Show",
            TitleSort = "show",
        };
        Season season = new()
        {
            Id = 1,
            SeasonNumber = 1,
            TvId = tv.Id,
            Tv = tv,
        };
        Episode first = BuildEpisode(40, tv, season);
        Episode second = BuildEpisode(41, tv, season);
        season.Episodes.Add(first);
        season.Episodes.Add(second);
        tv.Seasons.Add(season);

        _tvShowRepository
            .Setup(r => r.GetPlaylistAsync(It.IsAny<Guid>(), 9, "en", "US", default))
            .ReturnsAsync(tv);
        VideoPlaylistManager manager = CreateManager();

        (VideoPlaylistResponseDto? item, _) = await manager.GetPlaylist(
            Guid.NewGuid(),
            "tv",
            "9",
            itemId: 999,
            "en",
            "US"
        );

        item.Should().NotBeNull();
        item!.Id.Should().Be(40);
    }

    [Fact]
    public async Task GetPlaylist_TvType_RepositoryReturnsNull_YieldsEmptyPlaylistAndNullItem()
    {
        _tvShowRepository
            .Setup(r => r.GetPlaylistAsync(It.IsAny<Guid>(), 123, "en", "US", default))
            .ReturnsAsync((Tv?)null);
        VideoPlaylistManager manager = CreateManager();

        (VideoPlaylistResponseDto? item, List<VideoPlaylistResponseDto> playlist) =
            await manager.GetPlaylist(Guid.NewGuid(), "tv", "123", null, "en", "US");

        item.Should().BeNull();
        playlist.Should().BeEmpty();
    }

    [Fact]
    public async Task GetPlaylist_CollectionType_MapsMoviesWithOneBasedIndexAndCollectionId()
    {
        Collection collection = new() { Id = 500, Title = "Franchise" };
        Movie movieA = BuildMovie(1);
        Movie movieB = BuildMovie(2);
        collection.CollectionMovies.Add(new() { Collection = collection, Movie = movieA });
        collection.CollectionMovies.Add(new() { Collection = collection, Movie = movieB });

        _collectionRepository
            .Setup(r => r.GetCollectionPlaylistAsync(It.IsAny<Guid>(), 500, "en", "US", default))
            .ReturnsAsync(collection);
        VideoPlaylistManager manager = CreateManager();

        (_, List<VideoPlaylistResponseDto> playlist) = await manager.GetPlaylist(
            Guid.NewGuid(),
            "collection",
            "500",
            null,
            "en",
            "US"
        );

        playlist.Should().HaveCount(2);
        playlist.Select(p => p.Episode).Should().Equal(1, 2);
        playlist.Select(p => p.TmdbId).Should().AllBeEquivalentTo(500);
        ((object)playlist[0].PlaylistId).Should().Be("500");
    }

    [Fact]
    public async Task GetPlaylist_CollectionType_NoItemIdMatch_FallsBackToMostRecentProgress()
    {
        Collection collection = new() { Id = 501, Title = "Franchise" };
        Movie noProgress = BuildMovie(100);
        Movie older = BuildMovie(101, ProgressAt(new DateTime(2025, 1, 1)));
        Movie newer = BuildMovie(102, ProgressAt(new DateTime(2026, 6, 1)));
        collection.CollectionMovies.Add(new() { Collection = collection, Movie = noProgress });
        collection.CollectionMovies.Add(new() { Collection = collection, Movie = older });
        collection.CollectionMovies.Add(new() { Collection = collection, Movie = newer });

        _collectionRepository
            .Setup(r => r.GetCollectionPlaylistAsync(It.IsAny<Guid>(), 501, "en", "US", default))
            .ReturnsAsync(collection);
        VideoPlaylistManager manager = CreateManager();

        (VideoPlaylistResponseDto? item, _) = await manager.GetPlaylist(
            Guid.NewGuid(),
            "collection",
            "501",
            itemId: 999,
            "en",
            "US"
        );

        item.Should().NotBeNull();
        item!.Id.Should().Be(102, "the movie with the latest LastPlayedDate must win");
    }

    [Fact]
    public async Task GetPlaylist_SpecialType_EpisodeItem_UsesEpisodeConstructorBranch()
    {
        Ulid specialId = Ulid.NewUlid();
        Special special = new() { Id = specialId, Title = "Special" };
        Tv tv = new()
        {
            Id = 60,
            Title = "Show",
            TitleSort = "show",
        };
        Season season = new()
        {
            Id = 1,
            SeasonNumber = 1,
            TvId = tv.Id,
            Tv = tv,
        };
        Episode episode = BuildEpisode(70, tv, season);
        SpecialItem specialItem = new()
        {
            Order = 0,
            SpecialId = specialId,
            Special = special,
            EpisodeId = episode.Id,
            Episode = episode,
        };
        special.Items.Add(specialItem);

        _specialRepository
            .Setup(r => r.GetSpecialPlaylistAsync(It.IsAny<Guid>(), specialId, "en", "US", default))
            .ReturnsAsync(special);
        VideoPlaylistManager manager = CreateManager();

        (VideoPlaylistResponseDto? item, List<VideoPlaylistResponseDto> playlist) =
            await manager.GetPlaylist(
                Guid.NewGuid(),
                "specials",
                specialId.ToString(),
                null,
                "en",
                "US"
            );

        playlist.Should().ContainSingle();
        item.Should().NotBeNull();
        item!.Id.Should().Be(70);
        item.VideoType.Should().Be("tv");
    }

    [Fact]
    public async Task GetPlaylist_SpecialType_MovieItem_UsesMovieConstructorBranch()
    {
        Ulid specialId = Ulid.NewUlid();
        Special special = new() { Id = specialId, Title = "Special" };
        Movie movie = BuildMovie(80);
        SpecialItem specialItem = new()
        {
            Order = 0,
            SpecialId = specialId,
            Special = special,
            MovieId = movie.Id,
            Movie = movie,
        };
        special.Items.Add(specialItem);

        _specialRepository
            .Setup(r => r.GetSpecialPlaylistAsync(It.IsAny<Guid>(), specialId, "en", "US", default))
            .ReturnsAsync(special);
        VideoPlaylistManager manager = CreateManager();

        (VideoPlaylistResponseDto? item, List<VideoPlaylistResponseDto> playlist) =
            await manager.GetPlaylist(
                Guid.NewGuid(),
                "specials",
                specialId.ToString(),
                null,
                "en",
                "US"
            );

        playlist.Should().ContainSingle();
        item.Should().NotBeNull();
        item!.Id.Should().Be(80);
        item.VideoType.Should().Be("movie");
    }

    [Fact]
    public async Task GetPlaylist_SpecialType_NoItemIdMatch_FallsBackToMostRecentProgress()
    {
        Ulid specialId = Ulid.NewUlid();
        Special special = new() { Id = specialId, Title = "Special" };
        Movie noProgress = BuildMovie(200);
        Movie older = BuildMovie(201, ProgressAt(new DateTime(2025, 1, 1)));
        Movie newer = BuildMovie(202, ProgressAt(new DateTime(2026, 6, 1)));
        special.Items.Add(
            new()
            {
                Order = 0,
                SpecialId = specialId,
                Special = special,
                MovieId = noProgress.Id,
                Movie = noProgress,
            }
        );
        special.Items.Add(
            new()
            {
                Order = 1,
                SpecialId = specialId,
                Special = special,
                MovieId = older.Id,
                Movie = older,
            }
        );
        special.Items.Add(
            new()
            {
                Order = 2,
                SpecialId = specialId,
                Special = special,
                MovieId = newer.Id,
                Movie = newer,
            }
        );

        _specialRepository
            .Setup(r => r.GetSpecialPlaylistAsync(It.IsAny<Guid>(), specialId, "en", "US", default))
            .ReturnsAsync(special);
        VideoPlaylistManager manager = CreateManager();

        (VideoPlaylistResponseDto? item, _) = await manager.GetPlaylist(
            Guid.NewGuid(),
            "specials",
            specialId.ToString(),
            itemId: 999,
            "en",
            "US"
        );

        item.Should().NotBeNull();
        item!.Id.Should().Be(202, "the item with the latest LastPlayedDate must win");
    }

    [Fact]
    public async Task GetPlaylist_SpecialType_OrdersItemsByOrderField()
    {
        Ulid specialId = Ulid.NewUlid();
        Special special = new() { Id = specialId, Title = "Special" };
        Movie first = BuildMovie(90);
        Movie second = BuildMovie(91);
        // Deliberately added out of Order sequence.
        special.Items.Add(
            new()
            {
                Order = 1,
                SpecialId = specialId,
                Special = special,
                MovieId = second.Id,
                Movie = second,
            }
        );
        special.Items.Add(
            new()
            {
                Order = 0,
                SpecialId = specialId,
                Special = special,
                MovieId = first.Id,
                Movie = first,
            }
        );

        _specialRepository
            .Setup(r => r.GetSpecialPlaylistAsync(It.IsAny<Guid>(), specialId, "en", "US", default))
            .ReturnsAsync(special);
        VideoPlaylistManager manager = CreateManager();

        (_, List<VideoPlaylistResponseDto> playlist) = await manager.GetPlaylist(
            Guid.NewGuid(),
            "specials",
            specialId.ToString(),
            null,
            "en",
            "US"
        );

        playlist.Select(p => p.Id).Should().Equal(90, 91);
    }
}
