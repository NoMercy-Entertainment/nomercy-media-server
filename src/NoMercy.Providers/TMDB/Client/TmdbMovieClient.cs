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

using NoMercy.Providers.TMDB.Models.Genres;
using NoMercy.Providers.TMDB.Models.Movies;
using NoMercy.Providers.TMDB.Models.Shared;
using TmdbMovieCertifications = NoMercy.Providers.TMDB.Models.Certifications.TmdbMovieCertifications;

// ReSharper disable All

namespace NoMercy.Providers.TMDB.Client;

public class TmdbMovieClient : TmdbBaseClient, ITmdbMovieClient
{
    private readonly Func<TmdbMovieAppends?>? _mockAppendsProvider;

    public TmdbMovieClient(
        int? id = 0,
        string[]? appendices = null,
        Func<TmdbMovieAppends?>? mockAppendsProvider = null,
        string? language = "en-US"
    )
        : base((int)id!, language!)
    {
        _mockAppendsProvider = mockAppendsProvider;
    }

    public Task<TmdbMovieDetails?> Details(bool? priority = false)
    {
        return Get<TmdbMovieDetails>("movie/" + Id, priority: priority);
    }

    private Task<TmdbMovieAppends?> WithAppends(string[] appendices, bool? priority = false)
    {
        Dictionary<string, string?> queryParams = new()
        {
            ["append_to_response"] = string.Join(",", appendices),
        };

        return Get<TmdbMovieAppends>("movie/" + Id, queryParams, priority);
    }

    public Task<TmdbMovieAppends?> WithAllAppends(bool? priority = false)
    {
        if (_mockAppendsProvider != null)
        {
            return Task.FromResult(_mockAppendsProvider());
        }

        return WithAppends(
            [
                "alternative_titles",
                "release_dates",
                "changes",
                "credits",
                "keywords",
                "recommendations",
                "similar",
                "translations",
                "external_ids",
                "videos",
                "images",
                "watch/providers",
            ],
            priority
        );
    }

    public Task<TmdbMovieAggregatedCredits?> AggregatedCredits(bool? priority = false)
    {
        return Get<TmdbMovieAggregatedCredits>(
            "movie/" + Id + "/aggregate_credits",
            priority: priority
        );
    }

    public Task<TmdbMovieAlternativeTitles?> AlternativeTitles(bool? priority = false)
    {
        return Get<TmdbMovieAlternativeTitles>(
            "movie/" + Id + "/alternative_titles",
            priority: priority
        );
    }

    public Task<TmdbMovieChanges?> Changes(string startDate, string endDate)
    {
        Dictionary<string, string?> queryParams = new()
        {
            ["start_date"] = startDate,
            ["end_date"] = endDate,
        };

        return Get<TmdbMovieChanges>("movie/" + Id + "/changes", queryParams);
    }

    public Task<TmdbMovieCredits?> Credits(bool? priority = false)
    {
        return Get<TmdbMovieCredits>("movie/" + Id + "/credits", priority: priority);
    }

    public Task<TmdbMovieExternalIds?> ExternalIds(bool? priority = false)
    {
        return Get<TmdbMovieExternalIds>("movie/" + Id + "/external_ids", priority: priority);
    }

    public Task<TmdbImages?> Images(bool? priority = false)
    {
        return Get<TmdbImages>("movie/" + Id + "/images", priority: priority);
    }

    public Task<TmdbMovieKeywords?> Keywords(bool? priority = false)
    {
        return Get<TmdbMovieKeywords>("movie/" + Id + "/keywords", priority: priority);
    }

    public Task<TmdbMovieLists?> Lists(bool? priority = false)
    {
        return Get<TmdbMovieLists>("movie/" + Id + "/lists", priority: priority);
    }

    public Task<TmdbMovieRecommendations?> Recommendations(bool? priority = false)
    {
        return Get<TmdbMovieRecommendations>(
            "movie/" + Id + "/recommendations",
            priority: priority
        );
    }

    public Task<TmdbMovieReleaseDates?> ReleaseDates(bool? priority = false)
    {
        return Get<TmdbMovieReleaseDates>("movie/" + Id + "/release_dates", priority: priority);
    }

    public Task<TmdbMovieReviews?> Reviews(bool? priority = false)
    {
        return Get<TmdbMovieReviews>("movie/" + Id + "/reviews", priority: priority);
    }

    public Task<TmdbMovieSimilar?> Similar(bool? priority = false)
    {
        return Get<TmdbMovieSimilar>("movie/" + Id + "/similar", priority: priority);
    }

    public Task<TmdbSharedTranslations?> Translations(bool? priority = false)
    {
        return Get<TmdbSharedTranslations>("movie/" + Id + "/translations", priority: priority);
    }

    public Task<TmdbMovieVideos?> Videos(bool? priority = false)
    {
        return Get<TmdbMovieVideos>("movie/" + Id + "/videos", priority: priority);
    }

    public Task<TmdbWatchProviders?> WatchProviders(bool? priority = false)
    {
        return Get<TmdbWatchProviders>("movie/" + Id + "/watch/providers", priority: priority);
    }

    public Task<TmdbMovieLatest?> Latest(bool? priority = false)
    {
        return Get<TmdbMovieLatest>("movie/latest", priority: priority);
    }

    public Task<TmdbMovieNowPlaying?> NowPlaying(bool? priority = false)
    {
        return Get<TmdbMovieNowPlaying>("movie/now_playing", priority: priority);
    }

    public Task<List<TmdbMovie>?> Popular(int limit = 10)
    {
        return Paginated<TmdbMovie>("movie/popular", limit);
    }

    public Task<TmdbMovieTopRated?> TopRated(bool? priority = false)
    {
        return Get<TmdbMovieTopRated>("movie/top_rated", priority: priority);
    }

    public Task<TmdbMovieUpcoming?> Upcoming(bool? priority = false)
    {
        return Get<TmdbMovieUpcoming>("movie/upcoming", priority: priority);
    }

    public Task<TmdbMovieCertifications?> Certifications(bool? priority = false)
    {
        return Get<TmdbMovieCertifications>("certification/movie/list", priority: priority);
    }

    public Task<TmdbGenreMovies?> Genres(string language = "en", bool? priority = false)
    {
        return Get<TmdbGenreMovies>(
            "genre/movie/list",
            new Dictionary<string, string?> { ["language"] = language },
            priority: priority
        );
    }
}
