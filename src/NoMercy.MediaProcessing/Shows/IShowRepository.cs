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

using NoMercy.Database.Models.Common;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Media;
using NoMercy.Database.Models.Movies;
using NoMercy.Database.Models.TvShows;
using NoMercy.MediaProcessing.Common;
using NoMercy.Providers.TMDB.Models.TV;

namespace NoMercy.MediaProcessing.Shows;

public interface IShowRepository
{
    /// <summary>
    /// Upserts the show. <paramref name="folderDateIsReal"/> gates whether
    /// <see cref="Tv.CreatedAt"/> is written on an existing row: true means
    /// this call resolved a genuine on-disk folder date this pass, false
    /// means the folder lookup found nothing (a transient storage hiccup, or
    /// a manual add with no file yet) and the row's existing CreatedAt - if
    /// any - must be left alone rather than reset to "now". A brand new row
    /// always gets <paramref name="show"/>'s CreatedAt regardless, since
    /// there is no prior value to protect.
    /// </summary>
    Task AddAsync(Tv show, bool folderDateIsReal);
    Task Remove(int id);
    Task LinkToLibrary(Library library, Tv show, string? addedBy = null);
    Task<Library?> GetLibraryByTypeAsync(string type);

    /// <summary>
    /// Moves a Tv row into the library of the given type when it is not already
    /// there. Library membership only — no files move, nothing is re-scanned.
    /// Returns true when a row was actually moved.
    /// </summary>
    Task<bool> EnsureFiledUnderLibraryTypeAsync(int tvId, string libraryType);
    Task StoreAlternativeTitles(IEnumerable<AlternativeTitle> alternativeTitles);
    Task StoreTranslations(IEnumerable<Translation> translations);
    Task StoreContentRatings(IEnumerable<CertificationTv> certifications);
    Task StoreSimilar(IEnumerable<Similar> similar);
    Task StoreRecommendations(IEnumerable<Recommendation> recommendations);
    Task StoreVideos(IEnumerable<Media> videos);
    Task StoreImages(IEnumerable<Image> images);
    Task StoreKeywords(IEnumerable<Keyword> keywords);
    Task LinkKeywordsToTv(IEnumerable<KeywordTv> keywordTvs);
    Task StoreGenres(IEnumerable<GenreTv> genreTvs);

    Task StoreAnimeThemes(IEnumerable<AnimeThemeTv> animeThemeTvs);
    Task StoreAnimeDemographics(IEnumerable<AnimeDemographicTv> animeDemographicTvs);
    Task StoreAnimeSeason(int tvId, int year, string quarter);
    Task<int> ResolveAnimeThemeIdAsync(string name);
    Task<int> ResolveAnimeDemographicIdAsync(string name);
    Task<bool> HasAnimeThemesAsync(int tvId);
    Task<bool> HasAnimeDemographicsAsync(int tvId);

    Task StoreWatchProviders(List<WatchProvider> watchProviders);
    Task StoreNetworks(IEnumerable<Network> networks);
    Task StoreNetworkTvs(IEnumerable<NetworkTv> networkTvs);
    Task StoreCompanies(List<Company> companies);

    IEnumerable<CertificationTv> GetCertificationTvs(
        TmdbTvShowAppends show,
        IEnumerable<CertificationCriteria> certificationCriteria
    );

    Task StoreWatchProviderMedias(List<WatchProviderMedia> watchProviderMedias);
    Task StoreCompanyTvs(List<CompanyTv> companyTvs);
}
