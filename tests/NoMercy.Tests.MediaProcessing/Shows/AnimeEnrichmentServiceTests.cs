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

using System.Net;
using Moq;
using NoMercy.MediaProcessing.Shows;
using NoMercy.Providers.AniList;
using NoMercy.Providers.AniList.Models;
using NoMercy.Providers.Jikan;
using NoMercy.Providers.Jikan.Models;

namespace NoMercy.Tests.MediaProcessing.Shows;

public class AnimeEnrichmentServiceTests
{
    [Fact]
    public async Task EnrichTvAsync_NotClassifiedAnime_DoesNotCallProviders()
    {
        Mock<IMediaTypeClassifier> classifier = new();
        classifier
            .Setup(c => c.ClassifyAsync("Breaking Bad", 2008, It.IsAny<string[]?>()))
            .ReturnsAsync("tv");
        Mock<IAniListMetadataProvider> aniList = new();
        Mock<IJikanMetadataProvider> jikan = new();
        Mock<IShowRepository> showRepository = new();

        AnimeEnrichmentService service = new(
            classifier.Object,
            aniList.Object,
            jikan.Object,
            showRepository.Object,
            Mock.Of<NoMercy.MediaProcessing.Movies.IMovieRepository>()
        );

        await service.EnrichTvAsync(1, "Breaking Bad", 2008, ["US"]);

        showRepository.Verify(
            r =>
                r.StoreAnimeThemes(
                    It.IsAny<IEnumerable<NoMercy.Database.Models.TvShows.AnimeThemeTv>>()
                ),
            Times.Never
        );
        aniList.Verify(
            p => p.SearchAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<bool?>()),
            Times.Never
        );
        jikan.Verify(
            p => p.SearchAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<bool?>()),
            Times.Never
        );
    }

    [Fact]
    public async Task EnrichTvAsync_ClassifiedAnime_PersistsThemesFromAniListAndBackfillsFromJikan()
    {
        Mock<IMediaTypeClassifier> classifier = new();
        classifier
            .Setup(c => c.ClassifyAsync("One Piece", 1999, It.IsAny<string[]?>()))
            .ReturnsAsync("anime");

        Mock<IAniListMetadataProvider> aniList = new();
        aniList
            .Setup(p => p.SearchAsync("One Piece", 1999, It.IsAny<bool?>()))
            .ReturnsAsync(
                new AniListMedia
                {
                    Title = new() { Romaji = "One Piece" },
                    SeasonYear = 1999,
                    Season = "FALL",
                    Tags = [new() { Name = "Pirates", Category = "Setting-Universe" }],
                }
            );

        Mock<IJikanMetadataProvider> jikan = new();
        jikan
            .Setup(p => p.SearchAsync("One Piece", 1999, It.IsAny<bool?>()))
            .ReturnsAsync(
                new JikanAnime
                {
                    Titles = [new() { Type = "Default", Title = "One Piece" }],
                    Demographics = [new() { MalId = 27, Name = "Shounen" }],
                }
            );

        Mock<IShowRepository> showRepository = new();
        // "Pirates" has never been seen before, so the repository resolves
        // (creates) a real AnimeTheme row and hands back its database id —
        // this is the real resolve-or-create id, not a GetHashCode() stub.
        showRepository.Setup(r => r.ResolveAnimeThemeIdAsync("Pirates")).ReturnsAsync(501);
        // "Shounen" has never been seen before either, so the repository
        // resolves (creates) a real AnimeDemographic row and hands back its
        // database id — not the raw Jikan MalId (27), which is only stable
        // within MyAnimeList's own numbering.
        showRepository
            .Setup(r => r.ResolveAnimeDemographicIdAsync("Shounen"))
            .ReturnsAsync(701);

        AnimeEnrichmentService service = new(
            classifier.Object,
            aniList.Object,
            jikan.Object,
            showRepository.Object,
            Mock.Of<NoMercy.MediaProcessing.Movies.IMovieRepository>()
        );

        await service.EnrichTvAsync(42, "One Piece", 1999, ["JP"]);

        showRepository.Verify(r => r.ResolveAnimeThemeIdAsync("Pirates"), Times.Once);
        showRepository.Verify(r => r.ResolveAnimeDemographicIdAsync("Shounen"), Times.Once);
        showRepository.Verify(
            r =>
                r.StoreAnimeThemes(
                    It.Is<IEnumerable<NoMercy.Database.Models.TvShows.AnimeThemeTv>>(links =>
                        links.Any(l => l.TvId == 42 && l.AnimeThemeId == 501)
                    )
                ),
            Times.Once
        );
        showRepository.Verify(
            r =>
                r.StoreAnimeDemographics(
                    It.Is<IEnumerable<NoMercy.Database.Models.TvShows.AnimeDemographicTv>>(links =>
                        links.Any(l => l.TvId == 42 && l.AnimeDemographicId == 701)
                    )
                ),
            Times.Once
        );
        showRepository.Verify(r => r.StoreAnimeSeason(42, 1999, "FALL"), Times.Once);
    }

    // S4: AniList tags include non-theme categories (Cast-*, Technical, ...)
    // and adult-flagged tags; only Theme-*/Setting-* categories should reach
    // AnimeTheme, and IsAdult must always be excluded regardless of category.
    [Fact]
    public async Task EnrichTvAsync_FiltersOutNonThemeCategoriesAndAdultTags()
    {
        Mock<IMediaTypeClassifier> classifier = new();
        classifier
            .Setup(c => c.ClassifyAsync("Chainsaw Man", 2022, It.IsAny<string[]?>()))
            .ReturnsAsync("anime");

        Mock<IAniListMetadataProvider> aniList = new();
        aniList
            .Setup(p => p.SearchAsync("Chainsaw Man", 2022, It.IsAny<bool?>()))
            .ReturnsAsync(
                new AniListMedia
                {
                    Title = new() { Romaji = "Chainsaw Man" },
                    Tags =
                    [
                        new() { Name = "Gore", Category = "Setting-Universe" },
                        new() { Name = "Denji", Category = "Cast-Main Cast" },
                        new() { Name = "CGI", Category = "Technical" },
                        new()
                        {
                            Name = "Sexual Content",
                            Category = "Sexual Content",
                            IsAdult = true,
                        },
                        new()
                        {
                            Name = "Not Actually Adult But Flagged",
                            Category = "Theme-Action",
                            IsAdult = true,
                        },
                    ],
                }
            );

        Mock<IJikanMetadataProvider> jikan = new();
        jikan
            .Setup(p => p.SearchAsync("Chainsaw Man", 2022, It.IsAny<bool?>()))
            .ReturnsAsync(
                new JikanAnime { Titles = [new() { Type = "Default", Title = "Chainsaw Man" }] }
            );

        Mock<IShowRepository> showRepository = new();
        showRepository.Setup(r => r.ResolveAnimeThemeIdAsync("Gore")).ReturnsAsync(801);

        AnimeEnrichmentService service = new(
            classifier.Object,
            aniList.Object,
            jikan.Object,
            showRepository.Object,
            Mock.Of<NoMercy.MediaProcessing.Movies.IMovieRepository>()
        );

        await service.EnrichTvAsync(99, "Chainsaw Man", 2022, ["JP"]);

        showRepository.Verify(r => r.ResolveAnimeThemeIdAsync("Gore"), Times.Once);
        showRepository.Verify(r => r.ResolveAnimeThemeIdAsync("Denji"), Times.Never);
        showRepository.Verify(r => r.ResolveAnimeThemeIdAsync("CGI"), Times.Never);
        showRepository.Verify(r => r.ResolveAnimeThemeIdAsync("Sexual Content"), Times.Never);
        showRepository.Verify(
            r => r.ResolveAnimeThemeIdAsync("Not Actually Adult But Flagged"),
            Times.Never
        );
        showRepository.Verify(
            r =>
                r.StoreAnimeThemes(
                    It.Is<IEnumerable<NoMercy.Database.Models.TvShows.AnimeThemeTv>>(links =>
                        links.Count() == 1 && links.Any(l => l.TvId == 99 && l.AnimeThemeId == 801)
                    )
                ),
            Times.Once
        );
    }

    // S5: when AniList has no match at all, Jikan's Themes field should be
    // used as a fallback theme source, and a season row should still be
    // written from Jikan's Year with the "UNKNOWN" quarter sentinel since
    // Jikan carries no quarter concept and AnimeSeason.Quarter is non-nullable.
    [Fact]
    public async Task EnrichTvAsync_NoAniListMatch_FallsBackToJikanThemesAndYearOnlySeason()
    {
        Mock<IMediaTypeClassifier> classifier = new();
        classifier
            .Setup(c => c.ClassifyAsync("Obscure Anime", 2015, It.IsAny<string[]?>()))
            .ReturnsAsync("anime");

        Mock<IAniListMetadataProvider> aniList = new();
        aniList
            .Setup(p => p.SearchAsync("Obscure Anime", 2015, It.IsAny<bool?>()))
            .ReturnsAsync((AniListMedia?)null);

        Mock<IJikanMetadataProvider> jikan = new();
        jikan
            .Setup(p => p.SearchAsync("Obscure Anime", 2015, It.IsAny<bool?>()))
            .ReturnsAsync(
                new JikanAnime
                {
                    Titles = [new() { Type = "Default", Title = "Obscure Anime" }],
                    Themes = [new() { MalId = 12, Name = "Mecha" }],
                    Year = 2015,
                }
            );

        Mock<IShowRepository> showRepository = new();
        showRepository.Setup(r => r.ResolveAnimeThemeIdAsync("Mecha")).ReturnsAsync(901);

        AnimeEnrichmentService service = new(
            classifier.Object,
            aniList.Object,
            jikan.Object,
            showRepository.Object,
            Mock.Of<NoMercy.MediaProcessing.Movies.IMovieRepository>()
        );

        await service.EnrichTvAsync(77, "Obscure Anime", 2015, ["JP"]);

        showRepository.Verify(r => r.ResolveAnimeThemeIdAsync("Mecha"), Times.Once);
        showRepository.Verify(
            r =>
                r.StoreAnimeThemes(
                    It.Is<IEnumerable<NoMercy.Database.Models.TvShows.AnimeThemeTv>>(links =>
                        links.Any(l => l.TvId == 77 && l.AnimeThemeId == 901)
                    )
                ),
            Times.Once
        );
        showRepository.Verify(r => r.StoreAnimeSeason(77, 2015, "UNKNOWN"), Times.Once);
    }

    // Themes and demographics already stored: nothing is missing, so neither
    // provider should be called at all - re-running enrichment on an
    // already-complete title must not spend AniList/Jikan quota for no reason.
    // Includes the classifier itself: ClassifyAsync calls AniList/Jikan too,
    // so it must sit behind the same short-circuit, not run ahead of it.
    [Fact]
    public async Task EnrichTvAsync_ThemesAndDemographicsAlreadyStored_CallsNoProviders()
    {
        Mock<IMediaTypeClassifier> classifier = new();
        classifier
            .Setup(c => c.ClassifyAsync("One Piece", 1999, It.IsAny<string[]?>()))
            .ReturnsAsync("anime");

        Mock<IShowRepository> showRepository = new();
        showRepository.Setup(r => r.HasAnimeThemesAsync(42)).ReturnsAsync(true);
        showRepository.Setup(r => r.HasAnimeDemographicsAsync(42)).ReturnsAsync(true);

        Mock<IAniListMetadataProvider> aniList = new();
        Mock<IJikanMetadataProvider> jikan = new();

        AnimeEnrichmentService service = new(
            classifier.Object,
            aniList.Object,
            jikan.Object,
            showRepository.Object,
            Mock.Of<NoMercy.MediaProcessing.Movies.IMovieRepository>()
        );

        await service.EnrichTvAsync(42, "One Piece", 1999, ["JP"]);

        classifier.Verify(
            c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string[]?>()),
            Times.Never
        );
        aniList.Verify(
            p => p.SearchAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<bool?>()),
            Times.Never
        );
        jikan.Verify(
            p => p.SearchAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<bool?>()),
            Times.Never
        );
    }

    // Themes already stored, demographics still missing: AniList's tags have
    // nothing left to give, but its idMal cross-reference is still asked for
    // so Jikan can be queried by id - not by the unreliable search endpoint
    // (jikan-me/jikan-rest#610) - and not to re-derive themes/season it
    // already has.
    [Fact]
    public async Task EnrichTvAsync_ThemesStoredDemographicsMissing_QueriesJikanByIdForDemographics()
    {
        Mock<IMediaTypeClassifier> classifier = new();
        classifier
            .Setup(c => c.ClassifyAsync("One Piece", 1999, It.IsAny<string[]?>()))
            .ReturnsAsync("anime");

        Mock<IShowRepository> showRepository = new();
        showRepository.Setup(r => r.HasAnimeThemesAsync(42)).ReturnsAsync(true);
        showRepository.Setup(r => r.HasAnimeDemographicsAsync(42)).ReturnsAsync(false);
        showRepository.Setup(r => r.ResolveAnimeDemographicIdAsync("Shounen")).ReturnsAsync(701);

        Mock<IAniListMetadataProvider> aniList = new();
        aniList
            .Setup(p => p.SearchAsync("One Piece", 1999, It.IsAny<bool?>()))
            .ReturnsAsync(
                new AniListMedia
                {
                    IdMal = 21,
                    Title = new() { Romaji = "One Piece" },
                }
            );

        Mock<IJikanMetadataProvider> jikan = new();
        jikan
            .Setup(p => p.GetByIdAsync(21, It.IsAny<bool?>()))
            .ReturnsAsync(
                new JikanAnime
                {
                    Titles = [new() { Type = "Default", Title = "One Piece" }],
                    Demographics = [new() { MalId = 27, Name = "Shounen" }],
                }
            );

        AnimeEnrichmentService service = new(
            classifier.Object,
            aniList.Object,
            jikan.Object,
            showRepository.Object,
            Mock.Of<NoMercy.MediaProcessing.Movies.IMovieRepository>()
        );

        await service.EnrichTvAsync(42, "One Piece", 1999, ["JP"]);

        aniList.Verify(p => p.SearchAsync("One Piece", 1999, It.IsAny<bool?>()), Times.Once);
        jikan.Verify(p => p.GetByIdAsync(21, It.IsAny<bool?>()), Times.Once);
        jikan.Verify(
            p => p.SearchAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<bool?>()),
            Times.Never
        );
        showRepository.Verify(
            r =>
                r.StoreAnimeThemes(
                    It.IsAny<IEnumerable<NoMercy.Database.Models.TvShows.AnimeThemeTv>>()
                ),
            Times.Never
        );
        showRepository.Verify(
            r =>
                r.StoreAnimeDemographics(
                    It.Is<IEnumerable<NoMercy.Database.Models.TvShows.AnimeDemographicTv>>(links =>
                        links.Any(l => l.TvId == 42 && l.AnimeDemographicId == 701)
                    )
                ),
            Times.Once
        );
    }

    // Movie-side mirror of EnrichTvAsync_ThemesAndDemographicsAlreadyStored_CallsNoProviders -
    // EnrichMovieAsync runs the identical skip check against IMovieRepository
    // and must be pinned separately, since it is a distinct code path.
    [Fact]
    public async Task EnrichMovieAsync_ThemesAndDemographicsAlreadyStored_CallsNoProviders()
    {
        Mock<IMediaTypeClassifier> classifier = new();
        classifier
            .Setup(c => c.ClassifyAsync("Your Name", 2016, It.IsAny<string[]?>()))
            .ReturnsAsync("anime");

        Mock<NoMercy.MediaProcessing.Movies.IMovieRepository> movieRepository = new();
        movieRepository.Setup(r => r.HasAnimeThemesAsync(7)).ReturnsAsync(true);
        movieRepository.Setup(r => r.HasAnimeDemographicsAsync(7)).ReturnsAsync(true);

        Mock<IAniListMetadataProvider> aniList = new();
        Mock<IJikanMetadataProvider> jikan = new();

        AnimeEnrichmentService service = new(
            classifier.Object,
            aniList.Object,
            jikan.Object,
            Mock.Of<IShowRepository>(),
            movieRepository.Object
        );

        await service.EnrichMovieAsync(7, "Your Name", 2016, ["JP"]);

        aniList.Verify(
            p => p.SearchAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<bool?>()),
            Times.Never
        );
        jikan.Verify(
            p => p.SearchAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<bool?>()),
            Times.Never
        );
    }

    // Movie-side mirror of EnrichTvAsync_ThemesStoredDemographicsMissing_QueriesJikanByIdForDemographics.
    [Fact]
    public async Task EnrichMovieAsync_ThemesStoredDemographicsMissing_QueriesJikanByIdForDemographics()
    {
        Mock<IMediaTypeClassifier> classifier = new();
        classifier
            .Setup(c => c.ClassifyAsync("Your Name", 2016, It.IsAny<string[]?>()))
            .ReturnsAsync("anime");

        Mock<NoMercy.MediaProcessing.Movies.IMovieRepository> movieRepository = new();
        movieRepository.Setup(r => r.HasAnimeThemesAsync(7)).ReturnsAsync(true);
        movieRepository.Setup(r => r.HasAnimeDemographicsAsync(7)).ReturnsAsync(false);
        movieRepository.Setup(r => r.ResolveAnimeDemographicIdAsync("Shounen")).ReturnsAsync(702);

        Mock<IAniListMetadataProvider> aniList = new();
        aniList
            .Setup(p => p.SearchAsync("Your Name", 2016, It.IsAny<bool?>()))
            .ReturnsAsync(
                new AniListMedia
                {
                    IdMal = 32281,
                    Title = new() { Romaji = "Your Name" },
                }
            );

        Mock<IJikanMetadataProvider> jikan = new();
        jikan
            .Setup(p => p.GetByIdAsync(32281, It.IsAny<bool?>()))
            .ReturnsAsync(
                new JikanAnime
                {
                    Titles = [new() { Type = "Default", Title = "Your Name" }],
                    Demographics = [new() { MalId = 27, Name = "Shounen" }],
                }
            );

        AnimeEnrichmentService service = new(
            classifier.Object,
            aniList.Object,
            jikan.Object,
            Mock.Of<IShowRepository>(),
            movieRepository.Object
        );

        await service.EnrichMovieAsync(7, "Your Name", 2016, ["JP"]);

        aniList.Verify(p => p.SearchAsync("Your Name", 2016, It.IsAny<bool?>()), Times.Once);
        jikan.Verify(p => p.GetByIdAsync(32281, It.IsAny<bool?>()), Times.Once);
        jikan.Verify(
            p => p.SearchAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<bool?>()),
            Times.Never
        );
        movieRepository.Verify(
            r =>
                r.StoreAnimeThemes(
                    It.IsAny<IEnumerable<NoMercy.Database.Models.Movies.AnimeThemeMovie>>()
                ),
            Times.Never
        );
        movieRepository.Verify(
            r =>
                r.StoreAnimeDemographics(
                    It.Is<IEnumerable<NoMercy.Database.Models.Movies.AnimeDemographicMovie>>(
                        links => links.Any(l => l.MovieId == 7 && l.AnimeDemographicId == 702)
                    )
                ),
            Times.Once
        );
    }

    // A provider outage (Jikan/AniList throwing, e.g. a 502/503/504 that
    // exhausted the request queue's own retries) is a live scenario, not a
    // hypothetical: it must not propagate out of EnrichTvAsync, since
    // ShowManager.AddShowAsync awaits this call directly with no try/catch of
    // its own and would otherwise abort mid-import (content ratings and
    // translations never stored) on a transient upstream failure.
    [Fact]
    public async Task EnrichTvAsync_ClassifierThrows_DoesNotPropagate()
    {
        Mock<IMediaTypeClassifier> classifier = new();
        classifier
            .Setup(c => c.ClassifyAsync("One Piece", 1999, It.IsAny<string[]?>()))
            .ThrowsAsync(new HttpRequestException("504", null, HttpStatusCode.GatewayTimeout));

        Mock<IShowRepository> showRepository = new();
        showRepository.Setup(r => r.HasAnimeThemesAsync(42)).ReturnsAsync(false);
        showRepository.Setup(r => r.HasAnimeDemographicsAsync(42)).ReturnsAsync(false);

        AnimeEnrichmentService service = new(
            classifier.Object,
            Mock.Of<IAniListMetadataProvider>(),
            Mock.Of<IJikanMetadataProvider>(),
            showRepository.Object,
            Mock.Of<NoMercy.MediaProcessing.Movies.IMovieRepository>()
        );

        await service.EnrichTvAsync(42, "One Piece", 1999, ["JP"]);
    }

    // Movie-side mirror of EnrichTvAsync_ClassifierThrows_DoesNotPropagate:
    // MovieManager.AddMovieAsync also awaits EnrichMovieAsync with no
    // try/catch of its own.
    [Fact]
    public async Task EnrichMovieAsync_ClassifierThrows_DoesNotPropagate()
    {
        Mock<IMediaTypeClassifier> classifier = new();
        classifier
            .Setup(c => c.ClassifyAsync("Your Name", 2016, It.IsAny<string[]?>()))
            .ThrowsAsync(new HttpRequestException("504", null, HttpStatusCode.GatewayTimeout));

        Mock<NoMercy.MediaProcessing.Movies.IMovieRepository> movieRepository = new();
        movieRepository.Setup(r => r.HasAnimeThemesAsync(7)).ReturnsAsync(false);
        movieRepository.Setup(r => r.HasAnimeDemographicsAsync(7)).ReturnsAsync(false);

        AnimeEnrichmentService service = new(
            classifier.Object,
            Mock.Of<IAniListMetadataProvider>(),
            Mock.Of<IJikanMetadataProvider>(),
            Mock.Of<IShowRepository>(),
            movieRepository.Object
        );

        await service.EnrichMovieAsync(7, "Your Name", 2016, ["JP"]);
    }

    /// <summary>
    /// The classifier only chose a library at import time, so a show imported
    /// before it existed - or one whose AniList/Jikan lookup was inconclusive that
    /// day and defaulted to the tv library - stayed misfiled forever, and turned up
    /// under "Latest in Series" on the home screen.
    /// </summary>
    [Fact]
    public async Task EnrichTvAsync_ClassifiedAnime_IsRefiledUnderTheAnimeLibrary()
    {
        Mock<IMediaTypeClassifier> classifier = new();
        classifier
            .Setup(c => c.ClassifyAsync("Attack on Titan", 2013, It.IsAny<string[]?>()))
            .ReturnsAsync("anime");
        Mock<IShowRepository> showRepository = new();

        AnimeEnrichmentService service = new(
            classifier.Object,
            Mock.Of<IAniListMetadataProvider>(),
            Mock.Of<IJikanMetadataProvider>(),
            showRepository.Object,
            Mock.Of<NoMercy.MediaProcessing.Movies.IMovieRepository>()
        );

        await service.EnrichTvAsync(7, "Attack on Titan", 2013, ["JP"]);

        showRepository.Verify(r => r.EnsureFiledUnderLibraryTypeAsync(7, "anime"), Times.Once);
    }

    [Fact]
    public async Task EnrichTvAsync_NotClassifiedAnime_IsNeverRefiled()
    {
        Mock<IMediaTypeClassifier> classifier = new();
        classifier
            .Setup(c => c.ClassifyAsync("Breaking Bad", 2008, It.IsAny<string[]?>()))
            .ReturnsAsync("tv");
        Mock<IShowRepository> showRepository = new();

        AnimeEnrichmentService service = new(
            classifier.Object,
            Mock.Of<IAniListMetadataProvider>(),
            Mock.Of<IJikanMetadataProvider>(),
            showRepository.Object,
            Mock.Of<NoMercy.MediaProcessing.Movies.IMovieRepository>()
        );

        await service.EnrichTvAsync(1, "Breaking Bad", 2008, ["US"]);

        showRepository.Verify(
            r => r.EnsureFiledUnderLibraryTypeAsync(It.IsAny<int>(), It.IsAny<string>()),
            Times.Never
        );
    }

    /// <summary>
    /// An inconclusive lookup is not a verdict, so it must not move a row either
    /// way - the same rule the import path and the audit already follow.
    /// </summary>
    [Fact]
    public async Task EnrichTvAsync_InconclusiveLookup_IsNeverRefiled()
    {
        Mock<IMediaTypeClassifier> classifier = new();
        classifier
            .Setup(c =>
                c.ClassifyAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string[]?>())
            )
            .ReturnsAsync((string?)null);
        Mock<IShowRepository> showRepository = new();

        AnimeEnrichmentService service = new(
            classifier.Object,
            Mock.Of<IAniListMetadataProvider>(),
            Mock.Of<IJikanMetadataProvider>(),
            showRepository.Object,
            Mock.Of<NoMercy.MediaProcessing.Movies.IMovieRepository>()
        );

        await service.EnrichTvAsync(9, "Rate Limited Today", 2020, ["JP"]);

        showRepository.Verify(
            r => r.EnsureFiledUnderLibraryTypeAsync(It.IsAny<int>(), It.IsAny<string>()),
            Times.Never
        );
    }

    /// <summary>
    /// Themes and demographics only ever come from a successful anime match, so a
    /// fully-enriched show is provably anime. Placement is corrected from that
    /// stored proof, without spending an AniList/Jikan call to re-derive it.
    /// </summary>
    [Fact]
    public async Task EnrichTvAsync_AlreadyEnriched_IsRefiledWithoutCallingTheClassifier()
    {
        Mock<IMediaTypeClassifier> classifier = new();
        Mock<IShowRepository> showRepository = new();
        showRepository.Setup(r => r.HasAnimeThemesAsync(4)).ReturnsAsync(true);
        showRepository.Setup(r => r.HasAnimeDemographicsAsync(4)).ReturnsAsync(true);

        AnimeEnrichmentService service = new(
            classifier.Object,
            Mock.Of<IAniListMetadataProvider>(),
            Mock.Of<IJikanMetadataProvider>(),
            showRepository.Object,
            Mock.Of<NoMercy.MediaProcessing.Movies.IMovieRepository>()
        );

        await service.EnrichTvAsync(4, "Cowboy Bebop", 1998, ["JP"]);

        showRepository.Verify(r => r.EnsureFiledUnderLibraryTypeAsync(4, "anime"), Times.Once);
        classifier.Verify(
            c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string[]?>()),
            Times.Never
        );
    }
}
