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

using Microsoft.EntityFrameworkCore;
using NoMercy.Database;
using NoMercy.Database.Models.Common;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Media;
using NoMercy.Database.Models.Movies;
using NoMercy.MediaProcessing.Common;
using NoMercy.Providers.TMDB.Models.Movies;

namespace NoMercy.MediaProcessing.Movies;

public class MovieRepository(MediaContext context) : IMovieRepository
{
    public async Task Add(Movie movie, bool folderDateIsReal)
    {
        await context
            .Movies.Upsert(movie)
            .On(v => new { v.Id })
            .WhenMatched(
                (ts, ti) =>
                    new()
                    {
                        Id = ti.Id,
                        Backdrop = ti.Backdrop,
                        Duration = ti.Duration,
                        ReleaseDate = ti.ReleaseDate,
                        Homepage = ti.Homepage,
                        ImdbId = ti.ImdbId,
                        OriginalLanguage = ti.OriginalLanguage,
                        OriginCountry = ti.OriginCountry,
                        Overview = ti.Overview,
                        Popularity = ti.Popularity,
                        Poster = ti.Poster,
                        Status = ti.Status,
                        Tagline = ti.Tagline,
                        Title = ti.Title,
                        TitleSort = ti.TitleSort,
                        Trailer = ti.Trailer,
                        VoteAverage = ti.VoteAverage,
                        VoteCount = ti.VoteCount,
                        Folder = ti.Folder,
                        LibraryId = ti.LibraryId,
                    }
            )
            .RunAsync();

        // CreatedAt is [DatabaseGenerated(Computed)], so the Upsert insert
        // above never writes it (SQLite's CURRENT_TIMESTAMP default fires
        // instead) - this ExecuteUpdate is the only path that ever sets a
        // controlled value, for a brand new row and an existing one alike.
        // Only run it when this pass actually resolved a real on-disk folder
        // date: a failed/transient lookup must never stamp an already-dated
        // movie with a fresh "now" (see MovieManager.AddFromAppends). A
        // brand new row with no folder evidence at all is left on the
        // database default, which is the honest answer for "just added".
        if (folderDateIsReal)
            await context
                .Movies.Where(m => m.Id == movie.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.CreatedAt, t => movie.CreatedAt));

        await context.SaveChangesAsync();

        // Link any existing recommendation/similar rows that reference this movie as their target
        await context
            .Recommendations.Where(r => r.MediaId == movie.Id && r.MovieToId == null)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.MovieToId, movie.Id));

        await context
            .Similar.Where(r => r.MediaId == movie.Id && r.MovieToId == null)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.MovieToId, movie.Id));
    }

    public async Task Remove(int id)
    {
        // SQLite schema uses DeleteBehavior.Restrict globally. Disable FK
        // enforcement on this connection so the movie and all its dependents
        // are removed atomically, mirroring
        // Data.Repositories.MovieRepository.DeleteAsync.
        bool ownsConnection =
            context.Database.GetDbConnection().State != System.Data.ConnectionState.Open;
        if (ownsConnection)
            await context.Database.OpenConnectionAsync();

        try
        {
            await context.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF");
            try
            {
                await context.Movies.Where(movie => movie.Id == id).ExecuteDeleteAsync();
            }
            finally
            {
                await context.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON");
            }
        }
        finally
        {
            if (ownsConnection)
                await context.Database.CloseConnectionAsync();
        }
    }

    public Task LinkToLibrary(Library library, Movie movie)
    {
        return context
            .LibraryMovie.Upsert(new(library.Id, movie.Id))
            .On(v => new { v.LibraryId, v.MovieId })
            .WhenMatched((lts, lti) => new() { LibraryId = lti.LibraryId, MovieId = lti.MovieId })
            .RunAsync();
    }

    public Task StoreAlternativeTitles(IEnumerable<AlternativeTitle> alternativeTitles)
    {
        return context
            .AlternativeTitles.UpsertRange(alternativeTitles)
            .On(a => new { a.Title, a.MovieId })
            .WhenMatched(
                (ats, ati) =>
                    new()
                    {
                        Title = ati.Title,
                        Iso31661 = ati.Iso31661,
                        MovieId = ati.MovieId,
                    }
            )
            .RunAsync();
    }

    public Task StoreTranslations(IEnumerable<Translation> translations)
    {
        return context
            .Translations.UpsertRange(
                translations.Where(translation =>
                    translation.Title != "" || translation.Overview != ""
                )
            )
            .On(t => new
            {
                t.Iso31661,
                t.Iso6391,
                t.MovieId,
            })
            .WhenMatched(
                (ts, ti) =>
                    new()
                    {
                        Iso31661 = ti.Iso31661,
                        Iso6391 = ti.Iso6391,
                        Title = ti.Title,
                        EnglishName = ti.EnglishName,
                        Name = ti.Name,
                        Overview = ti.Overview,
                        Homepage = ti.Homepage,
                        Biography = ti.Biography,
                        MovieId = ti.MovieId,
                        SeasonId = ti.SeasonId,
                        EpisodeId = ti.EpisodeId,
                        CollectionId = ti.CollectionId,
                        PersonId = ti.PersonId,
                    }
            )
            .RunAsync();
    }

    public IEnumerable<CertificationMovie> GetCertificationMovies(
        TmdbMovieAppends movie,
        IEnumerable<CertificationCriteria> certificationCriteria
    )
    {
        return context
            .Certifications.AsEnumerable()
            .Where(c =>
                certificationCriteria.Any(cc =>
                    cc.Iso31661 == c.Iso31661 && cc.Certification == c.Rating
                )
            )
            .Select(c => new CertificationMovie { CertificationId = c.Id, MovieId = movie.Id });
    }

    public Task StoreContentRatings(IEnumerable<CertificationMovie> certifications)
    {
        return context
            .CertificationMovie.UpsertRange(certifications)
            .On(v => new { v.CertificationId, v.MovieId })
            .WhenMatched(
                (ts, ti) => new() { CertificationId = ti.CertificationId, MovieId = ti.MovieId }
            )
            .RunAsync();
    }

    public Task StoreSimilar(IEnumerable<Similar> similar)
    {
        return context
            .Similar.UpsertRange(similar)
            .On(v => new { v.MediaId, v.MovieFromId })
            .WhenMatched(
                (ts, ti) =>
                    new()
                    {
                        MovieToId = ti.MovieToId,
                        MovieFromId = ti.MovieFromId,
                        Overview = ti.Overview,
                        Title = ti.Title,
                        TitleSort = ti.TitleSort,
                        Backdrop = ti.Backdrop,
                        Poster = ti.Poster,
                        MediaId = ti.MediaId,
                    }
            )
            .RunAsync();
    }

    public Task StoreRecommendations(IEnumerable<Recommendation> recommendations)
    {
        return context
            .Recommendations.UpsertRange(recommendations)
            .On(v => new { v.MediaId, v.MovieFromId })
            .WhenMatched(
                (ts, ti) =>
                    new()
                    {
                        MovieToId = ti.MovieToId,
                        MovieFromId = ti.MovieFromId,
                        Overview = ti.Overview,
                        Title = ti.Title,
                        TitleSort = ti.TitleSort,
                        Backdrop = ti.Backdrop,
                        Poster = ti.Poster,
                        MediaId = ti.MediaId,
                    }
            )
            .RunAsync();
    }

    public Task StoreVideos(IEnumerable<Media> videos)
    {
        return context
            .Medias.UpsertRange(videos)
            .On(v => new { v.Src, v.MovieId })
            .WhenMatched(
                (ts, ti) =>
                    new()
                    {
                        Src = ti.Src,
                        Iso6391 = ti.Iso6391,
                        Type = ti.Type,
                        MovieId = ti.MovieId,
                        Name = ti.Name,
                        Site = ti.Site,
                        Size = ti.Size,
                    }
            )
            .RunAsync();
    }

    public Task StoreImages(IEnumerable<Image> images)
    {
        return context
            .Images.UpsertRange(images)
            .On(v => new { v.FilePath, v.MovieId })
            .WhenMatched(
                (ts, ti) =>
                    new()
                    {
                        AspectRatio = ti.AspectRatio,
                        FilePath = ti.FilePath,
                        Height = ti.Height,
                        Iso6391 = ti.Iso6391,
                        Site = ti.Site,
                        VoteAverage = ti.VoteAverage,
                        VoteCount = ti.VoteCount,
                        Width = ti.Width,
                        Type = ti.Type,
                        MovieId = ti.MovieId,
                    }
            )
            .RunAsync();
    }

    public Task StoreKeywords(IEnumerable<Keyword> keywords)
    {
        return context
            .Keywords.UpsertRange(keywords)
            .On(v => new { v.Id })
            .WhenMatched((ts, ti) => new() { Id = ti.Id, Name = ti.Name })
            .RunAsync();
    }

    public Task LinkKeywordsToMovie(IEnumerable<KeywordMovie> keywordMovies)
    {
        return context
            .KeywordMovie.UpsertRange(keywordMovies)
            .On(v => new { v.KeywordId, v.MovieId })
            .WhenMatched((ts, ti) => new() { KeywordId = ti.KeywordId, MovieId = ti.MovieId })
            .RunAsync();
    }

    public Task StoreGenres(IEnumerable<GenreMovie> genreMovies)
    {
        return context
            .GenreMovie.UpsertRange(genreMovies)
            .On(v => new { v.GenreId, v.MovieId })
            .WhenMatched((ts, ti) => new() { GenreId = ti.GenreId, MovieId = ti.MovieId })
            .RunAsync();
    }

    public Task StoreAnimeThemes(IEnumerable<AnimeThemeMovie> animeThemeMovies)
    {
        return context
            .AnimeThemeMovie.UpsertRange(animeThemeMovies.ToArray())
            .On(v => new { v.AnimeThemeId, v.MovieId })
            .WhenMatched((ts, ti) => new() { AnimeThemeId = ti.AnimeThemeId, MovieId = ti.MovieId })
            .RunAsync();
    }

    public Task StoreAnimeDemographics(IEnumerable<AnimeDemographicMovie> animeDemographicMovies)
    {
        return context
            .AnimeDemographicMovie.UpsertRange(animeDemographicMovies.ToArray())
            .On(v => new { v.AnimeDemographicId, v.MovieId })
            .WhenMatched(
                (ts, ti) =>
                    new() { AnimeDemographicId = ti.AnimeDemographicId, MovieId = ti.MovieId }
            )
            .RunAsync();
    }

    public async Task StoreAnimeSeason(int movieId, int year, string quarter)
    {
        int seasonId = await ResolveAnimeSeasonIdAsync(year, quarter);

        await context
            .AnimeSeasonMovie.Upsert(
                new AnimeSeasonMovie { AnimeSeasonId = seasonId, MovieId = movieId }
            )
            .On(v => new { v.AnimeSeasonId, v.MovieId })
            .WhenMatched(
                (ts, ti) => new() { AnimeSeasonId = ti.AnimeSeasonId, MovieId = ti.MovieId }
            )
            .RunAsync();
    }

    public async Task<int> ResolveAnimeThemeIdAsync(string name)
    {
        AnimeTheme? existing = await context
            .AnimeThemes.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Name == name);
        if (existing is not null)
            return existing.Id;

        AnimeTheme created = new() { Name = name };
        context.AnimeThemes.Add(created);
        await context.SaveChangesAsync();

        return created.Id;
    }

    public async Task<int> ResolveAnimeDemographicIdAsync(string name)
    {
        AnimeDemographic? existing = await context
            .AnimeDemographics.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Name == name);
        if (existing is not null)
            return existing.Id;

        AnimeDemographic created = new() { Name = name };
        context.AnimeDemographics.Add(created);
        await context.SaveChangesAsync();

        return created.Id;
    }

    public Task<bool> HasAnimeThemesAsync(int movieId) =>
        context.AnimeThemeMovie.AsNoTracking().AnyAsync(t => t.MovieId == movieId);

    public Task<bool> HasAnimeDemographicsAsync(int movieId) =>
        context.AnimeDemographicMovie.AsNoTracking().AnyAsync(d => d.MovieId == movieId);

    private async Task<int> ResolveAnimeSeasonIdAsync(int year, string quarter)
    {
        AnimeSeason? existing = await context
            .AnimeSeasons.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Year == year && s.Quarter == quarter);
        if (existing is not null)
            return existing.Id;

        AnimeSeason created = new() { Year = year, Quarter = quarter };
        context.AnimeSeasons.Add(created);
        await context.SaveChangesAsync();

        return created.Id;
    }

    public Task StoreCompanies(List<Company> companies)
    {
        return context
            .Companies.UpsertRange(companies)
            .On(v => new { v.Id })
            .WhenMatched(
                (ts, ti) =>
                    new()
                    {
                        Id = ti.Id,
                        Name = ti.Name,
                        Description = ti.Description,
                        Headquarters = ti.Headquarters,
                        Homepage = ti.Homepage,
                        Logo = ti.Logo,
                        OriginCountry = ti.OriginCountry,
                        ParentCompany = ti.ParentCompany,
                    }
            )
            .RunAsync();
    }

    public Task StoreCompanyMovies(List<CompanyMovie> companyMovies)
    {
        return context
            .CompanyMovie.UpsertRange(companyMovies)
            .On(v => new { v.CompanyId, v.MovieId })
            .WhenMatched((ts, ti) => new() { CompanyId = ti.CompanyId, MovieId = ti.MovieId })
            .RunAsync();
    }

    public Task StoreWatchProviders(List<WatchProvider> watchProviders)
    {
        return context
            .WatchProviders.UpsertRange(watchProviders)
            .On(v => new { v.Id })
            .WhenMatched(
                (ts, ti) =>
                    new()
                    {
                        Id = ti.Id,
                        Name = ti.Name,
                        Logo = ti.Logo,
                        DisplayPriority = ti.DisplayPriority,
                    }
            )
            .RunAsync();
    }

    public Task StoreWatchProviderMedias(List<WatchProviderMedia> watchProviderMedias)
    {
        return context
            .WatchProviderMedia.UpsertRange(watchProviderMedias)
            .On(v => new
            {
                v.WatchProviderId,
                v.CountryCode,
                v.ProviderType,
                v.MovieId,
                v.TvId,
            })
            .WhenMatched(
                (ts, ti) =>
                    new()
                    {
                        WatchProviderId = ti.WatchProviderId,
                        MovieId = ti.MovieId,
                        CountryCode = ti.CountryCode,
                        ProviderType = ti.ProviderType,
                    }
            )
            .RunAsync();
    }
}
