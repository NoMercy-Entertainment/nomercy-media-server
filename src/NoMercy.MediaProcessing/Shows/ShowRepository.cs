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
using NoMercy.Database.Models.TvShows;
using NoMercy.MediaProcessing.Common;
using NoMercy.Providers.TMDB.Models.TV;

namespace NoMercy.MediaProcessing.Shows;

public class ShowRepository(MediaContext context) : IShowRepository
{
    public async Task AddAsync(Tv tv, bool folderDateIsReal)
    {
        await context
            .Tvs.Upsert(tv)
            .On(v => new { v.Id })
            .WhenMatched(
                (ts, ti) =>
                    new()
                    {
                        Id = ti.Id,
                        Backdrop = ti.Backdrop,
                        Duration = ti.Duration,
                        FirstAirDate = ti.FirstAirDate,
                        Homepage = ti.Homepage,
                        ImdbId = ti.ImdbId,
                        InProduction = ti.InProduction,
                        LastEpisodeToAir = ti.LastEpisodeToAir,
                        NextEpisodeToAir = ti.NextEpisodeToAir,
                        NumberOfEpisodes = ti.NumberOfEpisodes,
                        NumberOfSeasons = ti.NumberOfSeasons,
                        OriginCountry = ti.OriginCountry,
                        OriginalLanguage = ti.OriginalLanguage,
                        Overview = ti.Overview,
                        Popularity = ti.Popularity,
                        Poster = ti.Poster,
                        SpokenLanguages = ti.SpokenLanguages,
                        Status = ti.Status,
                        Tagline = ti.Tagline,
                        Title = ti.Title,
                        TitleSort = ti.TitleSort,
                        Trailer = ti.Trailer,
                        TvdbId = ti.TvdbId,
                        Type = ti.Type,
                        VoteAverage = ti.VoteAverage,
                        VoteCount = ti.VoteCount,
                        Folder = ti.Folder,
                        LibraryId = ti.LibraryId,
                        MediaType = ti.MediaType,
                    }
            )
            .RunAsync();

        // CreatedAt is [DatabaseGenerated(Computed)], so the Upsert insert
        // above never writes it (SQLite's CURRENT_TIMESTAMP default fires
        // instead) - this ExecuteUpdate is the only path that ever sets a
        // controlled value, for a brand new row and an existing one alike.
        // Only run it when this pass actually resolved a real on-disk folder
        // date: a failed/transient lookup must never stamp an already-dated
        // show with a fresh "now" (see ShowManager.ResolveLibraryAndCreatedAtAsync).
        // A brand new row with no folder evidence at all is left on the
        // database default, which is the honest answer for "just added".
        if (folderDateIsReal)
            await context
                .Tvs.Where(t => t.Id == tv.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.CreatedAt, t => tv.CreatedAt));

        await context.SaveChangesAsync();

        // Link any existing recommendation/similar rows that reference this show as their target
        await context
            .Recommendations.Where(r => r.MediaId == tv.Id && r.TvToId == null)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.TvToId, tv.Id));

        await context
            .Similar.Where(r => r.MediaId == tv.Id && r.TvToId == null)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.TvToId, tv.Id));
    }

    public async Task Remove(int id)
    {
        // SQLite schema uses DeleteBehavior.Restrict globally. Disable FK
        // enforcement on this pinned connection so the show and all its
        // dependents are removed atomically, mirroring
        // Data.Repositories.TvShowRepository.DeleteAsync.
        bool ownsConnection =
            context.Database.GetDbConnection().State != System.Data.ConnectionState.Open;
        if (ownsConnection)
            await context.Database.OpenConnectionAsync();

        try
        {
            await context.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF");
            try
            {
                await context.Tvs.Where(tv => tv.Id == id).ExecuteDeleteAsync();
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

    public Task LinkToLibrary(Library library, Tv tv, string? addedBy = null)
    {
        return context
            .LibraryTv.Upsert(new(library.Id, tv.Id, addedBy))
            .On(v => new { v.LibraryId, v.TvId })
            // A link that already exists keeps the origin it was created with. A
            // scan re-running over a show the owner added must not quietly
            // downgrade it to something a file brought in.
            .WhenMatched((lts, lti) => new() { LibraryId = lti.LibraryId, TvId = lti.TvId })
            .RunAsync();
    }

    public Task<Library?> GetLibraryByTypeAsync(string type)
    {
        return context.Libraries.AsNoTracking().FirstOrDefaultAsync(l => l.Type == type);
    }

    public async Task<bool> EnsureFiledUnderLibraryTypeAsync(int tvId, string libraryType)
    {
        Library? target = await context
            .Libraries.AsNoTracking()
            .FirstOrDefaultAsync(library => library.Type == libraryType);
        if (target is null)
            return false;

        Tv? tv = await context.Tvs.FirstOrDefaultAsync(row => row.Id == tvId);
        if (tv is null || tv.LibraryId == target.Id)
            return false;

        tv.LibraryId = target.Id;
        await context.SaveChangesAsync();
        return true;
    }

    public Task StoreAlternativeTitles(IEnumerable<AlternativeTitle> alternativeTitles)
    {
        return context
            .AlternativeTitles.UpsertRange(alternativeTitles.ToArray())
            .On(a => new { a.Title, a.TvId })
            .WhenMatched(
                (ats, ati) =>
                    new()
                    {
                        Title = ati.Title,
                        Iso31661 = ati.Iso31661,
                        TvId = ati.TvId,
                    }
            )
            .RunAsync();
    }

    public Task StoreTranslations(IEnumerable<Translation> translations)
    {
        return context
            .Translations.UpsertRange(translations.ToArray())
            .On(t => new
            {
                t.Iso31661,
                t.Iso6391,
                t.TvId,
            })
            .WhenMatched(
                (ts, ti) =>
                    new()
                    {
                        Iso31661 = ti.Iso31661,
                        Iso6391 = ti.Iso6391,
                        Name = ti.Name,
                        EnglishName = ti.EnglishName,
                        Title = ti.Title,
                        Overview = ti.Overview,
                        Homepage = ti.Homepage,
                        Biography = ti.Biography,
                        TvId = ti.TvId,
                        SeasonId = ti.SeasonId,
                        EpisodeId = ti.EpisodeId,
                        MovieId = ti.MovieId,
                        CollectionId = ti.CollectionId,
                        PersonId = ti.PersonId,
                    }
            )
            .RunAsync();
    }

    public IEnumerable<CertificationTv> GetCertificationTvs(
        TmdbTvShowAppends tv,
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
            .Select(c => new CertificationTv { CertificationId = c.Id, TvId = tv.Id });
    }

    public Task StoreContentRatings(IEnumerable<CertificationTv> certifications)
    {
        return context
            .CertificationTv.UpsertRange(certifications.ToArray())
            .On(v => new { v.CertificationId, v.TvId })
            .WhenMatched((ts, ti) => new() { CertificationId = ti.CertificationId, TvId = ti.TvId })
            .RunAsync();
    }

    public Task StoreSimilar(IEnumerable<Similar> similar)
    {
        return context
            .Similar.UpsertRange(similar.ToArray())
            .On(v => new { v.MediaId, v.TvFromId })
            .WhenMatched(
                (ts, ti) =>
                    new()
                    {
                        TvToId = ti.TvToId,
                        TvFromId = ti.TvFromId,
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
            .Recommendations.UpsertRange(recommendations.ToArray())
            .On(v => new { v.MediaId, v.TvFromId })
            .WhenMatched(
                (ts, ti) =>
                    new()
                    {
                        TvToId = ti.TvToId,
                        TvFromId = ti.TvFromId,
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
            .Medias.UpsertRange(videos.ToArray())
            .On(v => new { v.Src, v.TvId })
            .WhenMatched(
                (ts, ti) =>
                    new()
                    {
                        Src = ti.Src,
                        Iso6391 = ti.Iso6391,
                        Type = ti.Type,
                        TvId = ti.TvId,
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
            .Images.UpsertRange(images.ToArray())
            .On(v => new { v.FilePath, v.TvId })
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
                        TvId = ti.TvId,
                    }
            )
            .RunAsync();
    }

    public Task StoreKeywords(IEnumerable<Keyword> keywords)
    {
        return context
            .Keywords.UpsertRange(keywords.ToArray())
            .On(v => new { v.Id })
            .WhenMatched((ts, ti) => new() { Id = ti.Id, Name = ti.Name })
            .RunAsync();
    }

    public Task LinkKeywordsToTv(IEnumerable<KeywordTv> keywordTvs)
    {
        return context
            .KeywordTv.UpsertRange(keywordTvs.ToArray())
            .On(v => new { v.KeywordId, v.TvId })
            .WhenMatched((ts, ti) => new() { KeywordId = ti.KeywordId, TvId = ti.TvId })
            .RunAsync();
    }

    public Task StoreGenres(IEnumerable<GenreTv> genreTvs)
    {
        return context
            .GenreTv.UpsertRange(genreTvs.ToArray())
            .On(v => new { v.GenreId, v.TvId })
            .WhenMatched((ts, ti) => new() { GenreId = ti.GenreId, TvId = ti.TvId })
            .RunAsync();
    }

    public Task StoreAnimeThemes(IEnumerable<AnimeThemeTv> animeThemeTvs)
    {
        return context
            .AnimeThemeTv.UpsertRange(animeThemeTvs.ToArray())
            .On(v => new { v.AnimeThemeId, v.TvId })
            .WhenMatched((ts, ti) => new() { AnimeThemeId = ti.AnimeThemeId, TvId = ti.TvId })
            .RunAsync();
    }

    public Task StoreAnimeDemographics(IEnumerable<AnimeDemographicTv> animeDemographicTvs)
    {
        return context
            .AnimeDemographicTv.UpsertRange(animeDemographicTvs.ToArray())
            .On(v => new { v.AnimeDemographicId, v.TvId })
            .WhenMatched(
                (ts, ti) => new() { AnimeDemographicId = ti.AnimeDemographicId, TvId = ti.TvId }
            )
            .RunAsync();
    }

    public async Task StoreAnimeSeason(int tvId, int year, string quarter)
    {
        int seasonId = await ResolveAnimeSeasonIdAsync(year, quarter);

        await context
            .AnimeSeasonTv.Upsert(new AnimeSeasonTv { AnimeSeasonId = seasonId, TvId = tvId })
            .On(v => new { v.AnimeSeasonId, v.TvId })
            .WhenMatched((ts, ti) => new() { AnimeSeasonId = ti.AnimeSeasonId, TvId = ti.TvId })
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

    public Task<bool> HasAnimeThemesAsync(int tvId) =>
        context.AnimeThemeTv.AsNoTracking().AnyAsync(t => t.TvId == tvId);

    public Task<bool> HasAnimeDemographicsAsync(int tvId) =>
        context.AnimeDemographicTv.AsNoTracking().AnyAsync(d => d.TvId == tvId);

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

    public async Task StoreNetworks(IEnumerable<Network> networks)
    {
        await context
            .Networks.UpsertRange(networks)
            .On(n => n.Id)
            .WhenMatched(n =>
                new()
                {
                    Name = n.Name,
                    Logo = n.Logo,
                    OriginCountry = n.OriginCountry,
                    Description = n.Description,
                    Headquarters = n.Headquarters,
                    Homepage = n.Homepage,
                }
            )
            .RunAsync();
    }

    public async Task StoreNetworkTvs(IEnumerable<NetworkTv> networkTvs)
    {
        await context
            .NetworkTv.UpsertRange(networkTvs)
            .On(nt => new { nt.NetworkId, nt.TvId })
            .WhenMatched(nt => new() { NetworkId = nt.NetworkId, TvId = nt.TvId })
            .RunAsync();
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

    public Task StoreCompanyTvs(List<CompanyTv> companyTvs)
    {
        return context
            .CompanyTv.UpsertRange(companyTvs.ToArray())
            .On(v => new { v.CompanyId, v.TvId })
            .WhenMatched((ts, ti) => new() { CompanyId = ti.CompanyId, TvId = ti.TvId })
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
            .WatchProviderMedia.UpsertRange(watchProviderMedias.ToArray())
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
                        TvId = ti.TvId,
                        ProviderType = ti.ProviderType,
                        CountryCode = ti.CountryCode,
                    }
            )
            .RunAsync();
    }
}
