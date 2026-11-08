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
using NoMercy.Data.DTOs.Specials;
using NoMercy.Data.Repositories;
using NoMercy.Database;
using NoMercy.Database.Models;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Media;
using NoMercy.Database.Models.Movies;
using NoMercy.Database.Models.TvShows;
using NoMercy.Database.Models.Users;

namespace NoMercy.Tests.Repositories;

/// <summary>
/// A show is available when a file belongs to a season 1+ episode, a movie when
/// it has a file. Every carousel and the available/total badge share one rule.
/// </summary>
public class MediaAvailabilityGateTests : IDisposable
{
    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Ulid LibraryId = Ulid.NewUlid();

    private const int SpecialsOnlyShow = 101;
    private const int SeasonOneShow = 102;
    private const int ManualShowWithNoFile = 103;
    private const int MovieWithFile = 201;
    private const int MovieWithoutFile = 202;

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MediaContext> _options;

    public MediaAvailabilityGateTests()
    {
        _connection = new("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<MediaContext>().UseSqlite(_connection).Options;

        using MediaContext context = new(_options);
        context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task HomeCards_DropAShowWhoseOnlyFilesAreSpecials()
    {
        await SeedAsync();
        HomeRepository repository = new(new MediaContext(_options), new Factory(_options));

        List<HomeTvCardDto> shows = await repository.GetHomeTvs(
            [SpecialsOnlyShow, SeasonOneShow, ManualShowWithNoFile],
            "en",
            "US"
        );

        shows.Select(show => show.Id).Should().BeEquivalentTo(new[] { SeasonOneShow });
        shows.Should().OnlyContain(show => show.EpisodesWithVideo > 0);
    }

    [Fact]
    public async Task HomeCards_KeepAMovieWithAFileAndDropOneWithout()
    {
        await SeedAsync();
        HomeRepository repository = new(new MediaContext(_options), new Factory(_options));

        List<HomeMovieCardDto> movies = await repository.GetHomeMovies(
            [MovieWithFile, MovieWithoutFile],
            "en",
            "US"
        );

        movies.Select(movie => movie.Id).Should().BeEquivalentTo(new[] { MovieWithFile });
    }

    [Fact]
    public async Task ContinueWatching_DropsAShowWhoseOnlyFilesAreSpecials()
    {
        await SeedAsync();
        HomeRepository repository = new(new MediaContext(_options), new Factory(_options));

        HashSet<UserData> rows = await repository.GetContinueWatchingAsync(UserId, "en", "US");

        rows.Where(row => row.TvId is not null)
            .Select(row => row.TvId)
            .Should()
            .BeEquivalentTo(new int?[] { SeasonOneShow });
        rows.Where(row => row.MovieId is not null)
            .Select(row => row.MovieId)
            .Should()
            .BeEquivalentTo(new int?[] { MovieWithFile });
    }

    [Fact]
    public async Task LibraryCarousel_DropsAShowWhoseOnlyFilesAreSpecials()
    {
        await SeedAsync();
        LibraryRepository repository = new(new Factory(_options));
        await using MediaContext context = new(_options);

        List<int> listed = [];
        await foreach (
            Tv show in repository.GetLibraryShows(
                context,
                UserId,
                LibraryId,
                "en",
                take: 50,
                skip: 0,
                orderByExpression: null,
                direction: null
            )
        )
        {
            listed.Add(show.Id);
        }

        listed.Should().BeEquivalentTo(new[] { SeasonOneShow });
    }

    [Fact]
    public async Task LibraryCards_DropSpecialsOnlyButKeepAManualShowWithNoFile()
    {
        await SeedAsync();
        LibraryRepository repository = new(new Factory(_options));

        List<int> listed = (await repository.GetLibraryTvCardsAsync(UserId, LibraryId, "US", 50, 0))
            .Select(card => card.Id)
            .ToList();

        listed.Should().BeEquivalentTo(new[] { SeasonOneShow, ManualShowWithNoFile });
    }

    [Fact]
    public async Task LibraryListing_KeepsAManualShowWithNoFile()
    {
        await SeedAsync();
        LibraryRepository repository = new(new Factory(_options));

        List<int> listed = (
            await repository.GetPaginatedLibraryShows(UserId, LibraryId, "s", "en", "US", 50, 0)
        )
            .Select(show => show.Id)
            .ToList();

        listed.Should().Contain(ManualShowWithNoFile).And.NotContain(SpecialsOnlyShow);
    }

    [Fact]
    public async Task SharedPredicate_MatchesTheRule()
    {
        await SeedAsync();
        await using MediaContext context = new(_options);

        List<int> shows = await context
            .Tvs.AsNoTracking()
            .Where(MediaAvailability.TvIsAvailable)
            .Select(tv => tv.Id)
            .ToListAsync();
        List<int> movies = await context
            .Movies.AsNoTracking()
            .Where(MediaAvailability.MovieIsAvailable)
            .Select(movie => movie.Id)
            .ToListAsync();

        shows.Should().BeEquivalentTo(new[] { SeasonOneShow });
        movies.Should().BeEquivalentTo(new[] { MovieWithFile });
    }

    [Fact]
    public async Task LibraryMovies_DropAMovieWhoseOnlyFileHasNoFolder()
    {
        await SeedAsync();
        await SeedCardShapesAsync();
        LibraryRepository repository = new(new Factory(_options));
        await using MediaContext context = new(_options);

        List<int> listed = [];
        await foreach (
            Movie movie in repository.GetLibraryMovies(
                context,
                UserId,
                LibraryId,
                "en",
                take: 50,
                skip: 0,
                orderByExpression: null,
                direction: null
            )
        )
        {
            listed.Add(movie.Id);
        }

        listed.Should().BeEquivalentTo(new[] { MovieWithFile, SecondMovieWithFile });
    }

    [Fact]
    public async Task ContinueWatching_DropsFolderlessMovieCollectionAndSpecialShapes()
    {
        await SeedAsync();
        await SeedCardShapesAsync();
        HomeRepository repository = new(new MediaContext(_options), new Factory(_options));

        HashSet<UserData> rows = await repository.GetContinueWatchingAsync(UserId, "en", "US");

        rows.Where(row => row.MovieId is not null)
            .Select(row => row.MovieId)
            .Should()
            .BeEquivalentTo(new int?[] { MovieWithFile });
        rows.Where(row => row.CollectionId is not null)
            .Select(row => row.CollectionId)
            .Should()
            .BeEquivalentTo(new int?[] { PlayableCollection });
        rows.Where(row => row.SpecialId is not null)
            .Select(row => row.SpecialId)
            .Should()
            .BeEquivalentTo(new Ulid?[] { PlayableSpecial, SeasonZeroSpecial });
    }

    [Fact]
    public async Task Favorites_DropFolderlessCollectionAndSpecialShapes()
    {
        await SeedAsync();
        await SeedCardShapesAsync();
        HomeRepository repository = new(new MediaContext(_options), new Factory(_options));

        FavoritesData favorites = await repository.GetFavoritesAsync(UserId, "en", "US");

        favorites
            .Collections.Select(c => c.Id)
            .Should()
            .BeEquivalentTo(new[] { PlayableCollection });
        favorites
            .Specials.Select(s => s.Id)
            .Should()
            .BeEquivalentTo(new[] { PlayableSpecial, SeasonZeroSpecial });
    }

    [Fact]
    public async Task SpecialCards_FilterBeforeThePageIsCut_AndKeepASeasonZeroSpecial()
    {
        await SeedAsync();
        await SeedCardShapesAsync();
        await using MediaContext context = new(_options);
        SpecialRepository repository = new(context, new Factory(_options));

        List<SpecialCardDto> cards = await repository.GetSpecialCardsAsync(UserId, "en", 2, 0);
        List<SpecialCardDto> itemCards = await repository.GetSpecialItemCardsAsync(
            UserId,
            "en",
            "US",
            2,
            0
        );

        cards.Select(c => c.Id).Should().Equal(PlayableSpecial, SeasonZeroSpecial);
        itemCards.Select(c => c.Id).Should().Equal(PlayableSpecial, SeasonZeroSpecial);
    }

    [Fact]
    public async Task VideoSearch_DropsTitlesWithNoPlayableFile()
    {
        await SeedAsync();
        await SeedCardShapesAsync();
        LibraryRepository repository = new(new Factory(_options));

        VideoSearchResults results = await repository.SearchVideoByTitleAsync("");

        results.Tvs.Select(tv => tv.Id).Should().BeEquivalentTo(new[] { SeasonOneShow });
        results
            .Movies.Select(movie => movie.Id)
            .Should()
            .BeEquivalentTo(new[] { MovieWithFile, SecondMovieWithFile });
    }

    private const int SecondMovieWithFile = 204;
    private const int FolderlessMovie = 203;
    private const int PlayableCollection = 301;
    private const int FolderlessCollection = 302;
    private static readonly Ulid FolderlessSpecial = Ulid.NewUlid();
    private static readonly Ulid EmptySpecial = Ulid.NewUlid();
    private static readonly Ulid PlayableSpecial = Ulid.NewUlid();
    private static readonly Ulid SeasonZeroSpecial = Ulid.NewUlid();

    private async Task SeedCardShapesAsync()
    {
        await using MediaContext context = new(_options);

        foreach (int id in new[] { FolderlessMovie, SecondMovieWithFile })
        {
            context.Movies.Add(
                new()
                {
                    Id = id,
                    Title = $"Movie {id}",
                    TitleSort = $"movie {id}",
                    LibraryId = LibraryId,
                }
            );
        }
        foreach (int id in new[] { PlayableCollection, FolderlessCollection })
        {
            context.Collections.Add(
                new()
                {
                    Id = id,
                    Title = $"Collection {id}",
                    LibraryId = LibraryId,
                }
            );
        }
        (Ulid Id, int Order, string Title)[] specials =
        [
            (FolderlessSpecial, 0, "Special A"),
            (EmptySpecial, 1, "Special B"),
            (PlayableSpecial, 2, "Special C"),
            (SeasonZeroSpecial, 3, "Special D"),
        ];
        foreach ((Ulid id, int order, string title) in specials)
        {
            context.Specials.Add(
                new()
                {
                    Id = id,
                    Order = order,
                    Title = title,
                    TitleSort = title,
                }
            );
            context.SpecialUser.Add(new(id, UserId));
        }
        await context.SaveChangesAsync();

        Ulid folderlessFileId = Ulid.NewUlid();
        Ulid secondFileId = Ulid.NewUlid();
        context.VideoFiles.AddRange(
            new VideoFile
            {
                Id = folderlessFileId,
                MovieId = FolderlessMovie,
                Folder = null,
                Filename = "gone.mkv",
                Share = "local",
            },
            new VideoFile
            {
                Id = secondFileId,
                MovieId = SecondMovieWithFile,
                Folder = "/Movies/Second",
                Filename = "second.mkv",
                Share = "local",
            }
        );
        context.CollectionMovie.AddRange(
            new(PlayableCollection, MovieWithFile),
            new(FolderlessCollection, FolderlessMovie)
        );
        context.CollectionUser.AddRange(
            new(PlayableCollection, UserId),
            new(FolderlessCollection, UserId)
        );
        context.SpecialItems.AddRange(
            new() { SpecialId = FolderlessSpecial, MovieId = FolderlessMovie },
            new() { SpecialId = PlayableSpecial, MovieId = MovieWithFile },
            new() { SpecialId = SeasonZeroSpecial, EpisodeId = 1 }
        );
        await context.SaveChangesAsync();

        Ulid playableFileId = (
            await context.VideoFiles.AsNoTracking().FirstAsync(v => v.MovieId == MovieWithFile)
        ).Id;
        context.UserData.AddRange(
            new UserData
            {
                UserId = UserId,
                MovieId = FolderlessMovie,
                VideoFileId = folderlessFileId,
                LastPlayedDate = "2026-01-02",
            },
            new UserData
            {
                UserId = UserId,
                CollectionId = PlayableCollection,
                VideoFileId = playableFileId,
                LastPlayedDate = "2026-01-02",
            },
            new UserData
            {
                UserId = UserId,
                CollectionId = FolderlessCollection,
                VideoFileId = folderlessFileId,
                LastPlayedDate = "2026-01-02",
            },
            new UserData
            {
                UserId = UserId,
                SpecialId = FolderlessSpecial,
                VideoFileId = folderlessFileId,
                LastPlayedDate = "2026-01-02",
            },
            new UserData
            {
                UserId = UserId,
                SpecialId = PlayableSpecial,
                VideoFileId = playableFileId,
                LastPlayedDate = "2026-01-02",
            },
            new UserData
            {
                UserId = UserId,
                SpecialId = SeasonZeroSpecial,
                VideoFileId = playableFileId,
                LastPlayedDate = "2026-01-02",
            }
        );
        await context.SaveChangesAsync();
    }

    private sealed class Factory(DbContextOptions<MediaContext> options)
        : IDbContextFactory<MediaContext>
    {
        public MediaContext CreateDbContext()
        {
            return new(options);
        }
    }

    private async Task SeedAsync()
    {
        await using MediaContext context = new(_options);

        context.Users.Add(new() { Id = UserId, Email = "owner@example.com" });
        context.Libraries.Add(
            new()
            {
                Id = LibraryId,
                Title = "Mixed",
                Type = "tv",
            }
        );
        await context.SaveChangesAsync();

        context.LibraryUser.Add(new() { LibraryId = LibraryId, UserId = UserId });
        foreach (int id in new[] { SpecialsOnlyShow, SeasonOneShow, ManualShowWithNoFile })
        {
            context.Tvs.Add(
                new()
                {
                    Id = id,
                    Title = $"Show {id}",
                    TitleSort = $"show {id}",
                    LibraryId = LibraryId,
                }
            );
        }
        foreach (int id in new[] { MovieWithFile, MovieWithoutFile })
        {
            context.Movies.Add(
                new()
                {
                    Id = id,
                    Title = $"Movie {id}",
                    TitleSort = $"movie {id}",
                    LibraryId = LibraryId,
                }
            );
        }
        await context.SaveChangesAsync();

        context.LibraryTv.AddRange(
            new(LibraryId, SpecialsOnlyShow),
            new(LibraryId, SeasonOneShow),
            new(LibraryId, ManualShowWithNoFile, LibraryLinkOrigin.Manual)
        );
        await context.SaveChangesAsync();

        Ulid specialsFileId = await AddEpisodeWithFileAsync(
            context,
            1,
            SpecialsOnlyShow,
            seasonNumber: 0
        );
        Ulid seasonOneFileId = await AddEpisodeWithFileAsync(
            context,
            2,
            SeasonOneShow,
            seasonNumber: 1
        );

        Ulid movieFileId = Ulid.NewUlid();
        context.VideoFiles.Add(
            new()
            {
                Id = movieFileId,
                MovieId = MovieWithFile,
                Folder = "/Movies/Movie",
                Filename = "movie.mkv",
                Share = "local",
            }
        );

        foreach (
            (int tvId, Ulid fileId) in new[]
            {
                (SpecialsOnlyShow, specialsFileId),
                (SeasonOneShow, seasonOneFileId),
            }
        )
        {
            context.UserData.Add(
                new()
                {
                    UserId = UserId,
                    TvId = tvId,
                    VideoFileId = fileId,
                    LastPlayedDate = "2026-01-01",
                }
            );
        }
        foreach (int movieId in new[] { MovieWithFile, MovieWithoutFile })
        {
            context.UserData.Add(
                new()
                {
                    UserId = UserId,
                    MovieId = movieId,
                    VideoFileId = movieFileId,
                    LastPlayedDate = "2026-01-01",
                }
            );
        }
        await context.SaveChangesAsync();
    }

    private static async Task<Ulid> AddEpisodeWithFileAsync(
        MediaContext context,
        int id,
        int tvId,
        int seasonNumber
    )
    {
        Season season = new()
        {
            Id = id,
            TvId = tvId,
            SeasonNumber = seasonNumber,
            Title = $"Season {seasonNumber}",
        };
        context.Seasons.Add(season);
        await context.SaveChangesAsync();

        Episode episode = new()
        {
            Id = id,
            TvId = tvId,
            SeasonId = season.Id,
            SeasonNumber = seasonNumber,
            EpisodeNumber = 1,
            Title = "One",
        };
        context.Episodes.Add(episode);
        await context.SaveChangesAsync();

        Ulid fileId = Ulid.NewUlid();
        context.VideoFiles.Add(
            new()
            {
                Id = fileId,
                EpisodeId = episode.Id,
                Folder = "/Show",
                Filename = $"s{seasonNumber}e1.mkv",
                Share = "local",
            }
        );
        await context.SaveChangesAsync();
        return fileId;
    }
}
