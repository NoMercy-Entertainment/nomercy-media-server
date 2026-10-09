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

using NoMercy.Api.DTOs.Media;
using NoMercy.Database.Models.Common;
using NoMercy.Database.Models.Movies;
using NoMercy.Database.Models.People;
using NoMercy.Database.Models.TvShows;
using NoMercy.NmSystem.Extensions;
using Xunit;
using MediaEntity = NoMercy.Database.Models.Media.Media;

namespace NoMercy.Tests.Api.Media;

[Trait("Category", "Unit")]
public class LibraryResponseItemDtoTests
{
    private static Movie BuildMovie(int id = 1)
    {
        Movie movie = new()
        {
            Id = id,
            Title = "Test Movie",
            Backdrop = "/movie-backdrop.jpg",
            Overview = "Movie overview",
            Poster = "/movie-poster.jpg",
            ReleaseDate = new(2020, 5, 1),
            Video = "movie-video-key",
        };

        movie.Images.Add(new() { Type = "logo", FilePath = "/movie-logo.png" });

        Genre genre = new() { Id = 1, Name = "Action" };
        movie.GenreMovies.Add(
            new()
            {
                GenreId = 1,
                Genre = genre,
                MovieId = id,
            }
        );

        movie.VideoFiles.Add(
            new()
            {
                Filename = "movie.mkv",
                HostFolder = "/x",
                Folder = "/x",
            }
        );

        movie.Media.Add(
            new()
            {
                Site = "YouTube",
                Src = "yt-key",
                Name = "Trailer",
            }
        );
        movie.Media.Add(
            new()
            {
                Site = "Vimeo",
                Src = "vimeo-key",
                Name = "Other",
            }
        );

        return movie;
    }

    [Fact]
    public void Ctor_LibraryMovie_MapsFieldsFromNestedMovieAndFiltersYouTubeVideos()
    {
        Movie movie = BuildMovie();
        LibraryMovie libraryMovie = new() { Movie = movie };

        LibraryResponseItemDto dto = new(libraryMovie);

        Assert.Equal("1", dto.Id);
        Assert.Equal("/movie-backdrop.jpg", dto.Backdrop);
        Assert.Equal("/movie-logo.png", dto.Logo);
        Assert.Equal("movie", dto.MediaType);
        Assert.Equal("movie", dto.Type);
        Assert.Equal(2020, dto.Year);
        Assert.Equal("Movie overview", dto.Overview);
        Assert.Equal("/movie-poster.jpg", dto.Poster);
        Assert.Equal("Test Movie", dto.Title);
        Assert.Equal("Test Movie".TitleSort(movie.ReleaseDate), dto.TitleSort);
        Assert.Equal("/movie/1", dto.Link.ToString());
        Assert.Equal("movie-video-key", dto.VideoId);

        dto.Genres.Should().ContainSingle();
        Assert.Equal("Action", dto.Genres!.First().Name);

        dto.Videos.Should().ContainSingle();
        Assert.Equal("yt-key", dto.Videos.First().Src);
    }

    [Fact]
    public void Ctor_LibraryMovie_NoLogoImage_LeavesLogoNull()
    {
        Movie movie = BuildMovie();
        movie.Images.Clear();
        LibraryMovie libraryMovie = new() { Movie = movie };

        LibraryResponseItemDto dto = new(libraryMovie);

        Assert.Null(dto.Logo);
    }

    private static Tv BuildTv(int id = 2)
    {
        Tv tv = new()
        {
            Id = id,
            Title = "Test Show",
            Backdrop = "/tv-backdrop.jpg",
            Overview = "Tv overview",
            Poster = "/tv-poster.jpg",
            FirstAirDate = new(2019, 8, 1),
            Trailer = "tv-trailer-key",
            NumberOfEpisodes = 5,
        };

        tv.Images.Add(new() { Type = "logo", FilePath = "/tv-logo.png" });

        Genre genre = new() { Id = 2, Name = "Drama" };
        tv.GenreTvs.Add(
            new()
            {
                GenreId = 2,
                Genre = genre,
                TvId = id,
            }
        );

        Episode episodeWithFile = new() { Id = 10 };
        episodeWithFile.VideoFiles.Add(
            new()
            {
                Filename = "e1.mkv",
                HostFolder = "/x",
                Folder = "/x",
            }
        );

        Episode episodeWithoutFolder = new() { Id = 11 };
        episodeWithoutFolder.VideoFiles.Add(
            new()
            {
                Filename = "e2.mkv",
                HostFolder = "/x",
                Folder = null,
            }
        );

        tv.Episodes.Add(episodeWithFile);
        tv.Episodes.Add(episodeWithoutFolder);

        tv.Media.Add(
            new()
            {
                Site = "YouTube",
                Src = "tv-yt-key",
                Name = "Trailer",
            }
        );

        return tv;
    }

    [Fact]
    public void Ctor_LibraryTv_MapsFieldsAndCountsEpisodesWithFolder()
    {
        Tv tv = BuildTv();
        LibraryTv libraryTv = new() { Tv = tv };

        LibraryResponseItemDto dto = new(libraryTv);

        Assert.Equal("2", dto.Id);
        Assert.Equal("/tv-backdrop.jpg", dto.Backdrop);
        Assert.Equal("/tv-logo.png", dto.Logo);
        Assert.Equal("tv", dto.MediaType);
        Assert.Equal("tv", dto.Type);
        Assert.Equal(2019, dto.Year);
        Assert.Equal("Tv overview", dto.Overview);
        Assert.Equal("Test Show", dto.Title);
        Assert.Equal("Test Show".TitleSort(tv.FirstAirDate), dto.TitleSort);
        Assert.Equal("/tv/2", dto.Link.ToString());
        Assert.Equal("tv-trailer-key", dto.VideoId);
        Assert.Equal(5, dto.NumberOfItems);
        // Only the episode whose video file has a non-null Folder counts.
        Assert.Equal(1, dto.HaveItems);

        dto.Genres.Should().ContainSingle();
        dto.Videos.Should().ContainSingle();
        Assert.Equal("tv-yt-key", dto.Videos.First().Src);
    }

    [Fact]
    public void Ctor_MovieDirect_MapsFieldsAndCountsFilesWithFolder()
    {
        Movie movie = BuildMovie(3);
        movie.VideoFiles.Add(
            new()
            {
                Filename = "extra.mkv",
                HostFolder = "/x",
                Folder = null,
            }
        );

        LibraryResponseItemDto dto = new(movie);

        Assert.Equal("3", dto.Id);
        Assert.Equal(1, dto.NumberOfItems);
        // Only the first video file has a non-null Folder; the extra one doesn't count.
        Assert.Equal(1, dto.HaveItems);
        Assert.Equal("movie", dto.Type);
        Assert.Equal("movie-video-key", dto.VideoId);
        dto.Videos.Should().ContainSingle();
    }

    [Fact]
    public void Ctor_TvDirect_MapsFieldsAndCountsEpisodesWithFolder()
    {
        Tv tv = BuildTv(4);

        LibraryResponseItemDto dto = new(tv);

        Assert.Equal("4", dto.Id);
        Assert.Equal(5, dto.NumberOfItems);
        Assert.Equal(1, dto.HaveItems);
        Assert.Equal("tv", dto.Type);
        Assert.Equal("tv", dto.MediaType);
        Assert.Equal("tv-trailer-key", dto.VideoId);
    }

    [Fact]
    public void Ctor_CollectionMovie_MapsFieldsFromNestedMovie()
    {
        Movie movie = BuildMovie(5);
        CollectionMovie collectionMovie = new() { Movie = movie };

        LibraryResponseItemDto dto = new(collectionMovie);

        Assert.Equal("5", dto.Id);
        Assert.Equal("movie", dto.Type);
        Assert.Equal(1, dto.NumberOfItems);
        Assert.Equal(1, dto.HaveItems);
        Assert.Equal("movie-video-key", dto.VideoId);
        dto.Genres.Should().ContainSingle();
    }

    private static Collection BuildCollection()
    {
        Collection collection = new()
        {
            Id = 100,
            Title = "The Original Collection",
            Backdrop = "/collection-backdrop.jpg",
            Poster = "/collection-poster.jpg",
            Parts = 2,
        };

        collection.Images.Add(new() { Type = "logo", FilePath = "/collection-logo.png" });

        Movie earlyMovie = new()
        {
            Id = 6,
            Title = "Early Movie",
            ReleaseDate = new(2001, 1, 1),
            Video = "early-video-key",
        };
        earlyMovie.VideoFiles.Add(
            new()
            {
                Filename = "early.mkv",
                HostFolder = "/x",
                Folder = "/x",
            }
        );

        Genre genre = new() { Id = 3, Name = "Sci-Fi" };
        earlyMovie.GenreMovies.Add(
            new()
            {
                GenreId = 3,
                Genre = genre,
                MovieId = 6,
            }
        );

        Movie laterMovie = new()
        {
            Id = 7,
            Title = "Later Movie",
            ReleaseDate = new(2010, 1, 1),
            Video = "later-video-key",
        };

        // Inserted out of date order on purpose: VideoId comes from FirstOrDefault
        // (insertion order), while Year/TitleSort come from MinBy (release date order).
        collection.CollectionMovies.Add(new() { CollectionId = 100, Movie = laterMovie });
        collection.CollectionMovies.Add(new() { CollectionId = 100, Movie = earlyMovie });

        return collection;
    }

    [Fact]
    public void Ctor_Collection_UsesEarliestMovieForYearAndTitleSort_NoTranslation()
    {
        Collection collection = BuildCollection();

        LibraryResponseItemDto dto = new(collection);

        Assert.Equal("100", dto.Id);
        // The displayed name drops a trailing "Collection" (see CollectionTitleFormatter);
        // TitleSort keeps the raw stored title since it is a sort key, not a display value.
        Assert.Equal("The Original", dto.Title);
        Assert.Equal(string.Empty, dto.Overview);
        Assert.Equal("/collection-backdrop.jpg", dto.Backdrop);
        Assert.Equal("/collection-logo.png", dto.Logo);
        Assert.Equal(2001, dto.Year); // earliest release date among CollectionMovies
        Assert.Equal("/collection-poster.jpg", dto.Poster);
        Assert.Equal("The Original Collection".TitleSort(2001), dto.TitleSort);
        Assert.Equal("specials", dto.Type);
        Assert.Equal("specials", dto.MediaType);
        Assert.Equal("/collection/100", dto.Link.ToString());
        Assert.Equal(2, dto.NumberOfItems);
        // Only earlyMovie has a video file with a non-null Folder.
        Assert.Equal(1, dto.HaveItems);
        // VideoId comes from FirstOrDefault() (insertion order = laterMovie), not
        // from the release-date-earliest movie used for Year/TitleSort.
        Assert.Equal("later-video-key", dto.VideoId);

        dto.Genres.Should().ContainSingle();
    }

    [Fact]
    public void Ctor_Collection_UsesTranslationTitleAndOverview_WhenPresent()
    {
        Collection collection = BuildCollection();
        collection.Translations.Add(
            new() { Title = "De Originele Collectie", Overview = "Nederlandse samenvatting." }
        );

        LibraryResponseItemDto dto = new(collection);

        Assert.Equal("De Originele Collectie", dto.Title);
        Assert.Equal("Nederlandse samenvatting.", dto.Overview);
    }

    [Fact]
    public void Ctor_Collection_EmptyCollectionMovies_YearIsNull()
    {
        Collection collection = new()
        {
            Id = 101,
            Title = "Empty Collection",
            Parts = 0,
        };

        LibraryResponseItemDto dto = new(collection);

        Assert.Null(dto.Year);
        Assert.Equal("Empty Collection".TitleSort((int?)null), dto.TitleSort);
        Assert.Null(dto.VideoId);
    }

    [Fact]
    public void Ctor_Special_MapsMinimalFieldsIntoNameNotTitle()
    {
        Special special = new()
        {
            Id = Ulid.NewUlid(),
            Title = "A Special Event",
            Overview = "special overview",
            Backdrop = "/special-backdrop.jpg",
            Poster = "/special-poster.jpg",
        };

        LibraryResponseItemDto dto = new(special);

        Assert.Equal(special.Id.ToString(), dto.Id);
        Assert.Equal("A Special Event", dto.Name);
        Assert.Equal(string.Empty, dto.Title); // Title is never set for Special, only Name
        Assert.Equal("special overview", dto.Overview);
        Assert.Equal("/special-backdrop.jpg", dto.Backdrop);
        Assert.Equal("specials", dto.MediaType);
        Assert.Equal("specials", dto.Type);
        Assert.Equal($"/specials/{special.Id}", dto.Link.ToString());
        Assert.Equal("/special-poster.jpg", dto.Poster);
        Assert.Equal("A Special Event".TitleSort(), dto.TitleSort);
    }

    [Fact]
    public void Ctor_Special_NullTitleAndOverview_FallBackToEmpty()
    {
        Special special = new()
        {
            Id = Ulid.NewUlid(),
            Title = null,
            Overview = null,
        };

        LibraryResponseItemDto dto = new(special);

        Assert.Equal(string.Empty, dto.Name);
        Assert.Equal(string.Empty, dto.Overview);
    }

    [Fact]
    public void Ctor_Person_UsesTranslationTitleAndBiography_WhenPresent()
    {
        Person person = new()
        {
            Id = 50,
            Name = "Original Name",
            Biography = "Original biography.",
            Profile = "/person-profile.jpg",
            Gender = "Female",
        };
        person.Translations.Add(
            new() { Title = "Vertaalde Naam", Biography = "Vertaalde biografie." }
        );

        LibraryResponseItemDto dto = new(person);

        Assert.Equal("50", dto.Id);
        Assert.Equal("Vertaalde Naam", dto.Name);
        Assert.Equal("Vertaalde biografie.", dto.Overview);
        Assert.Equal("person", dto.MediaType);
        Assert.Equal("person", dto.Type);
        Assert.Equal("/person/50", dto.Link.ToString());
        // TitleSort always normalizes the raw (untranslated) Person.Name via the
        // shared TitleSort() helper, same as every sibling constructor in this file
        // and required by AlphaBucket's "normalized TitleSort, never raw title" contract.
        Assert.Equal("Original Name".TitleSort(), dto.TitleSort);
        Assert.Equal("/person-profile.jpg", dto.Poster);
    }

    [Fact]
    public void Ctor_Person_NoTranslation_FallsBackToOwnNameAndBiography()
    {
        Person person = new()
        {
            Id = 51,
            Name = "The Plain Name",
            Biography = null,
        };

        LibraryResponseItemDto dto = new(person);

        Assert.Equal("The Plain Name", dto.Name);
        Assert.Equal(string.Empty, dto.Overview);
        // Leading article is stripped by the shared TitleSort() helper.
        Assert.Equal("plain.name", dto.TitleSort);
    }

    [Fact]
    public void Ctor_Movie_TitleWithColon_UsesReleaseDateInTitleSort()
    {
        Movie movie = BuildMovie(8);
        movie.Title = "Se7en: Director's Cut";
        movie.ReleaseDate = new(1995, 1, 1);

        LibraryResponseItemDto dto = new(movie);

        // Pin the DTO's forwarding of (title, release date) to the shared TitleSort
        // helper, rather than re-deriving that helper's own regex behaviour here.
        Assert.Equal("Se7en: Director's Cut".TitleSort(movie.ReleaseDate), dto.TitleSort);
    }

    [Fact]
    public void Ctor_LibraryMovie_NoYouTubeMedia_VideosEmpty()
    {
        Movie movie = BuildMovie(9);
        movie.Media.Clear();
        movie.Media.Add(new MediaEntity { Site = "Vimeo", Src = "vimeo-only" });
        LibraryMovie libraryMovie = new() { Movie = movie };

        LibraryResponseItemDto dto = new(libraryMovie);

        Assert.Empty(dto.Videos);
    }
}
