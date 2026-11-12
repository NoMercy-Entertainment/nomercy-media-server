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

using NoMercy.Database.Models.Libraries;
using NoMercy.Providers.MusicBrainz.Client;
using NoMercy.Providers.MusicBrainz.Models;
using NoMercy.Providers.TMDB.Client;
using NoMercy.Providers.TMDB.Models.Movies;
using NoMercy.Providers.TMDB.Models.Shared;
using NoMercy.Providers.TMDB.Models.TV;
using NoMercy.Providers.TVDB.Client;
using NoMercy.Providers.TVDB.Models.Search;
using NoMercy.Providers.TVDB.Models.Shared;

namespace NoMercy.MediaProcessing.Inbox;

public sealed class TmdbMusicBrainzMetadataProbe : IInboxMetadataProbe
{
    public async Task<CandidateMatch[]> SearchMoviesAsync(
        string title,
        int? year,
        CancellationToken ct
    )
    {
        using TmdbSearchClient client = new();
        TmdbPaginatedResponse<TmdbMovie>? response = await client.Movie(title, year?.ToString());

        if (response?.Results is null || response.Results.Count == 0)
            return await SearchTvdbAsync(title, year, isTv: false);

        return
        [
            .. response
                .Results.Take(5)
                .Select(m => new CandidateMatch
                {
                    Provider = "tmdb",
                    ExternalId = m.Id.ToString(),
                    Title = m.Title,
                    Year = m.ReleaseDate?.Year,
                    PosterPath = m.PosterPath,
                    Score = NormalizePopularity(m.Popularity, m.VoteAverage),
                }),
        ];
    }

    public async Task<CandidateMatch[]> SearchTvAsync(string title, int? year, CancellationToken ct)
    {
        using TmdbSearchClient client = new();
        TmdbPaginatedResponse<TmdbTvShow>? response = await client.TvShow(title, year?.ToString());

        if (response?.Results is null || response.Results.Count == 0)
            return await SearchTvdbAsync(title, year, isTv: true);

        return
        [
            .. response
                .Results.Take(5)
                .Select(show => new CandidateMatch
                {
                    Provider = "tmdb",
                    ExternalId = show.Id.ToString(),
                    Title = show.Name,
                    Year = show.FirstAirDate?.Year,
                    PosterPath = show.PosterPath,
                    Score = NormalizePopularity(show.Popularity, show.VoteAverage),
                }),
        ];
    }

    public async Task<CandidateMatch?> LookupMusicReleaseAsync(Guid releaseId, CancellationToken ct)
    {
        using MusicBrainzReleaseClient client = new();
        MusicBrainzReleaseAppends? release = await client.WithAllAppends(releaseId);

        if (release is null)
            return null;

        string artist = release.ArtistCredit.Select(ac => ac.Name).FirstOrDefault() ?? string.Empty;

        int? year = release
            .ReleaseEvents?.Select(e => e.DateTime?.Year)
            .Where(y => y.HasValue)
            .Select(y => y!.Value)
            .FirstOrDefault();

        return new()
        {
            Provider = "musicbrainz",
            ExternalId = releaseId.ToString(),
            Title = string.IsNullOrEmpty(artist) ? release.Title : $"{artist} – {release.Title}",
            Year = year == 0 ? null : year,
            PosterPath = null,
            Score = 1.0,
        };
    }

    private static async Task<CandidateMatch[]> SearchTvdbAsync(string title, int? year, bool isTv)
    {
        using TvdbSearchClient client = new();
        TvdbSearchResponse? response = isTv
            ? await client.Series(title, year)
            : await client.Movie(title, year);

        if (response?.Data is null)
            return [];

        List<CandidateMatch> matches = [];
        foreach (TvdbSearchResult result in response.Data)
        {
            // Import jobs still require TMDB ids. A TVDB-only id must never be
            // handed to them as if it belonged to TMDB.
            TvdbRemoteId? tmdb = result.RemoteIds?.FirstOrDefault(remote =>
                (
                    remote.SourceName?.Contains("themoviedb", StringComparison.OrdinalIgnoreCase)
                    ?? false
                ) || string.Equals(remote.SourceName, "tmdb", StringComparison.OrdinalIgnoreCase)
            );
            if (tmdb is null || !int.TryParse(tmdb.Id, out int tmdbId) || tmdbId <= 0)
                continue;

            if (string.IsNullOrWhiteSpace(result.Name))
                continue;

            matches.Add(
                new()
                {
                    Provider = "tmdb",
                    ExternalId = tmdbId.ToString(),
                    Title = result.Name,
                    Year = int.TryParse(result.Year, out int resultYear) ? resultYear : null,
                    // TVDB images are absolute URLs; the existing poster path is TMDB-relative.
                    PosterPath = null,
                    // A cross-reference identifies the work but carries no TMDB ranking signal.
                    Score = 0.49,
                }
            );
            if (matches.Count == 5)
                break;
        }

        return [.. matches];
    }

    private static double NormalizePopularity(double popularity, double voteAverage)
    {
        double popScore = Math.Min(popularity / 100.0, 1.0);
        double voteScore = voteAverage / 10.0;
        return Math.Round((popScore * 0.4) + (voteScore * 0.6), 4);
    }
}
