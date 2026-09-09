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
using NoMercy.MediaProcessing.Common;
using NoMercy.Providers.TMDB.Models.Movies;

namespace NoMercy.MediaProcessing.Movies;

public interface IMovieRepository
{
    /// <summary>
    /// Upserts the movie. <paramref name="folderDateIsReal"/> gates whether
    /// <see cref="Movie.CreatedAt"/> is written on an existing row: true
    /// means this call resolved a genuine on-disk folder date this pass,
    /// false means the folder lookup found nothing (a transient storage
    /// hiccup, or a manual add with no file yet) and the row's existing
    /// CreatedAt - if any - must be left alone rather than reset to "now".
    /// A brand new row always gets <paramref name="movie"/>'s CreatedAt
    /// regardless, since there is no prior value to protect.
    /// </summary>
    Task Add(Movie movie, bool folderDateIsReal);
    Task LinkToLibrary(Library library, Movie movie);
    Task Remove(int id);
    Task StoreAlternativeTitles(IEnumerable<AlternativeTitle> alternativeTitles);
    Task StoreTranslations(IEnumerable<Translation> translations);
    Task StoreContentRatings(IEnumerable<CertificationMovie> certifications);
    Task StoreSimilar(IEnumerable<Similar> similar);
    Task StoreRecommendations(IEnumerable<Recommendation> recommendations);
    Task StoreVideos(IEnumerable<Media> videos);
    Task StoreImages(IEnumerable<Image> images);
    Task StoreKeywords(IEnumerable<Keyword> keywords);
    Task LinkKeywordsToMovie(IEnumerable<KeywordMovie> keywordMovies);
    Task StoreGenres(IEnumerable<GenreMovie> genreMovies);

    Task StoreAnimeThemes(IEnumerable<AnimeThemeMovie> animeThemeMovies);
    Task StoreAnimeDemographics(IEnumerable<AnimeDemographicMovie> animeDemographicMovies);
    Task StoreAnimeSeason(int movieId, int year, string quarter);
    Task<int> ResolveAnimeThemeIdAsync(string name);
    Task<int> ResolveAnimeDemographicIdAsync(string name);
    Task<bool> HasAnimeThemesAsync(int movieId);
    Task<bool> HasAnimeDemographicsAsync(int movieId);

    Task StoreWatchProviders(List<WatchProvider> watchProviders);
    Task StoreCompanies(List<Company> companies);

    IEnumerable<CertificationMovie> GetCertificationMovies(
        TmdbMovieAppends movie,
        IEnumerable<CertificationCriteria> certificationCriteria
    );

    Task StoreWatchProviderMedias(List<WatchProviderMedia> watchProviderMedias);
    Task StoreCompanyMovies(List<CompanyMovie> companyMovies);
}
