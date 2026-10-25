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

using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using NoMercy.NmSystem.Dto;
using NoMercy.OpticalMedia.Sources;
using NoMercy.Providers.TMDB.Client;
using NoMercy.Providers.TMDB.Models.Movies;
using NoMercy.Providers.TMDB.Models.Season;
using NoMercy.Providers.TMDB.Models.Shared;
using NoMercy.Providers.TMDB.Models.TV;

namespace NoMercy.OpticalMedia.Metadata;

/// <summary>
/// Identifies DVD and Blu-ray discs by searching TMDB. Matches are ranked
/// by a blended confidence score (string similarity + runtime proximity).
///
/// For TV discs a second pass walks the season/episode list to find the best
/// season/episode fit based on disc duration.
///
/// Implements <see cref="IDiscIdentifier"/>; dispatched by
/// <see cref="DiscIdentificationService"/>.
/// </summary>
public sealed partial class VideoDiscIdentifier(ILogger<VideoDiscIdentifier> logger)
    : IDiscIdentifier
{
    private const int MaxCandidatesPerType = 5;

    /// <summary>Confidence threshold above which a match is auto-applied.</summary>
    public const double AutoApplyThreshold = 0.85;

    public bool CanHandle(OpticalDiscType type) =>
        type == OpticalDiscType.Dvd || type == OpticalDiscType.BluRay;

    public async Task<DiscIdentification> IdentifyAsync(DiscInfo disc, CancellationToken ct)
    {
        // Prefer the embedded Blu-ray title (bdmt_*.xml) over the raw volume label.
        string? querySource = disc.DiscTitle ?? disc.DiscLabel;

        if (string.IsNullOrWhiteSpace(querySource))
        {
            logger.LogInformation(
                "VideoDiscIdentifier skipped — disc has no label or embedded title (type={Type})",
                disc.Type
            );
            return NeedsManual();
        }

        string fullQuery = NormalizeLabel(querySource);
        if (string.IsNullOrWhiteSpace(fullQuery))
        {
            logger.LogInformation(
                "VideoDiscIdentifier skipped — normalised query is empty (raw={Raw})",
                querySource
            );
            return NeedsManual();
        }

        int discDurationSec = disc.MainTitleDurationSec;

        List<DiscCandidate> all =
        [
            .. await SearchAsync(fullQuery, MediaType.Movie, ct, discDurationSec),
            .. await SearchAsync(fullQuery, MediaType.TvShow, ct, discDurationSec),
        ];

        if (all.Count == 0)
        {
            string firstWord = fullQuery.Split(' ', 2)[0];
            if (!firstWord.Equals(fullQuery, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogInformation(
                    "VideoDiscIdentifier fallback '{Query}' → '{FirstWord}'",
                    fullQuery,
                    firstWord
                );
                all.AddRange(await SearchAsync(firstWord, MediaType.Movie, ct, discDurationSec));
                all.AddRange(await SearchAsync(firstWord, MediaType.TvShow, ct, discDurationSec));
            }
        }

        if (all.Count == 0)
        {
            logger.LogInformation(
                "VideoDiscIdentifier found no TMDB candidates for '{Query}'",
                fullQuery
            );
            return NeedsManual();
        }

        // For TV matches, attempt per-episode resolution when disc duration is known.
        List<DiscCandidate> resolved = [];
        foreach (DiscCandidate candidate in all)
        {
            if (
                candidate.Source == "tmdb"
                && candidate.Type == MediaType.TvShow
                && !string.IsNullOrEmpty(candidate.StableId)
                && discDurationSec > 0
            )
            {
                DiscCandidate enriched = await TryResolveEpisodeAsync(
                    candidate,
                    discDurationSec,
                    ct
                );
                resolved.Add(enriched);
            }
            else
            {
                resolved.Add(candidate);
            }
        }

        DiscCandidate[] ranked = [.. resolved.OrderByDescending(c => c.Confidence)];

        double topConfidence = ranked.Length > 0 ? ranked[0].Confidence : 0;
        bool autoApply = topConfidence >= AutoApplyThreshold;

        logger.LogInformation(
            "VideoDiscIdentifier '{Query}' (duration={Sec}s): {Count} candidates, topConfidence={Conf:F4}, autoApply={Auto}",
            fullQuery,
            discDurationSec,
            ranked.Length,
            topConfidence,
            autoApply
        );

        return new(
            Kind: MediaKind.Movie,
            Candidates: ranked,
            TopConfidence: topConfidence,
            AutoApply: autoApply,
            NeedsManualAssignment: false
        );
    }

    private async Task<IEnumerable<DiscCandidate>> SearchAsync(
        string query,
        MediaType type,
        CancellationToken ct,
        int discDurationSec
    )
    {
        try
        {
            return type switch
            {
                MediaType.Movie => await SearchMoviesAsync(query, discDurationSec),
                MediaType.TvShow => await SearchTvShowsAsync(query, discDurationSec),
                _ => [],
            };
        }
        catch (Exception ex)
        {
            logger.LogInformation(
                ex,
                "TMDB search failed for {Type} '{Query}': {Message}",
                type,
                query,
                ex.Message
            );
            return [];
        }
    }

    /// <summary>
    /// Manual title search — used by the dashboard's search-box endpoint.
    /// Searches both Movies and TvShows and returns ranked candidates.
    /// </summary>
    public async Task<DiscCandidate[]> SearchAsync(
        string query,
        MediaType type,
        CancellationToken ct
    )
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        try
        {
            return type switch
            {
                MediaType.Movie => [.. await SearchMoviesAsync(query, discDurationSec: 0)],
                MediaType.TvShow => [.. await SearchTvShowsAsync(query, discDurationSec: 0)],
                _ => [],
            };
        }
        catch (Exception ex)
        {
            logger.LogInformation(
                ex,
                "TMDB manual search failed for {Type} '{Query}': {Message}",
                type,
                query,
                ex.Message
            );
            return [];
        }
    }

    private async Task<IEnumerable<DiscCandidate>> SearchMoviesAsync(
        string query,
        int discDurationSec
    )
    {
        TmdbSearchClient client = new();
        TmdbPaginatedResponse<TmdbMovie>? response = await client.Movie(query);
        if (response?.Results is null || response.Results.Count == 0)
            return [];

        List<DiscCandidate> matches = [];
        int rank = 0;
        foreach (TmdbMovie movie in response.Results.Take(MaxCandidatesPerType))
        {
            int? runtimeMin = await FetchMovieRuntimeAsync(movie.Id);
            double confidence = BlendConfidence(
                query,
                movie.Title ?? movie.OriginalTitle ?? "",
                rank,
                discDurationSec,
                runtimeMin
            );
            matches.Add(
                new(
                    Source: "tmdb",
                    StableId: movie.Id.ToString(CultureInfo.InvariantCulture),
                    Title: movie.Title ?? movie.OriginalTitle ?? string.Empty,
                    Year: ParseYear(movie.ReleaseDate),
                    PosterUrl: PosterUrl(movie.PosterPath),
                    BackdropUrl: BackdropUrl(movie.BackdropPath),
                    Confidence: confidence,
                    Type: MediaType.Movie
                )
            );
            rank++;
        }
        return matches;
    }

    private async Task<IEnumerable<DiscCandidate>> SearchTvShowsAsync(
        string query,
        int discDurationSec
    )
    {
        TmdbSearchClient client = new();
        TmdbPaginatedResponse<TmdbTvShow>? response = await client.TvShow(query);
        if (response?.Results is null || response.Results.Count == 0)
            return [];

        List<DiscCandidate> matches = [];
        int rank = 0;
        foreach (TmdbTvShow show in response.Results.Take(MaxCandidatesPerType))
        {
            int? episodeRunMin = await FetchTvEpisodeRunTimeAsync(show.Id);
            double confidence = BlendConfidence(
                query,
                show.Name ?? show.OriginalName ?? "",
                rank,
                discDurationSec,
                episodeRunMin
            );
            matches.Add(
                new(
                    Source: "tmdb",
                    StableId: show.Id.ToString(CultureInfo.InvariantCulture),
                    Title: show.Name ?? show.OriginalName ?? string.Empty,
                    Year: ParseYear(show.FirstAirDate),
                    PosterUrl: PosterUrl(show.PosterPath),
                    BackdropUrl: BackdropUrl(show.BackdropPath),
                    Confidence: confidence,
                    Type: MediaType.TvShow
                )
            );
            rank++;
        }
        return matches;
    }

    /// <summary>
    /// For a candidate that resolved from the TV-show search, attempts to
    /// determine which season and starting episode the disc contains.
    ///
    /// The disc duration is compared against hypotheses:
    ///   - Single-episode: disc ≈ 1 × episodeRuntime
    ///   - Multi-episode:  disc ≈ N × episodeRuntime  (N episodes starting at ep 1)
    ///
    /// When individual episode runtimes are not available from TMDB, the
    /// show-level EpisodeRunTime is used as a uniform estimate.
    ///
    /// BUG FIX: the original implementation used a constant delta
    /// (<c>Math.Abs(discDurationSec - showRunSec)</c>) for every episode
    /// slot, so the comparison never changed across loop iterations and
    /// always produced episode 1. The corrected logic makes the expected
    /// duration depend on how many episodes fit (<c>epIdx * showRunSec</c>),
    /// producing a real per-hypothesis delta.
    /// </summary>
    private async Task<DiscCandidate> TryResolveEpisodeAsync(
        DiscCandidate candidate,
        int discDurationSec,
        CancellationToken ct
    )
    {
        if (!int.TryParse(candidate.StableId, out int showId))
            return candidate;

        try
        {
            TmdbTvClient tvClient = new(showId);
            TmdbTvShowDetails? showDetails = await tvClient.Details();
            if (showDetails?.Seasons is null || showDetails.Seasons.Count == 0)
                return candidate;

            int showRunMin = showDetails.EpisodeRunTime?.FirstOrDefault() ?? 0;
            if (showRunMin <= 0)
                return candidate;

            double showRunSec = showRunMin * 60.0;

            int bestSeasonNumber = 0;
            int bestEpisodeCount = 1;
            double bestDelta = double.MaxValue;

            foreach (TmdbSeason season in showDetails.Seasons.Where(s => s.SeasonNumber > 0))
            {
                ct.ThrowIfCancellationRequested();

                TmdbSeasonClient seasonClient = new(showId, season.SeasonNumber);
                TmdbSeasonDetails? seasonDetails = await seasonClient.Details();
                if (seasonDetails?.Episodes is null || seasonDetails.Episodes.Length == 0)
                    continue;

                int episodeCount = seasonDetails.Episodes.Length;

                // Test each hypothesis: disc contains episodes 1..epIdx
                // so expected total runtime = epIdx * showRunSec.
                for (int epIdx = 1; epIdx <= episodeCount; epIdx++)
                {
                    double expectedSec = epIdx * showRunSec;
                    double delta = Math.Abs(discDurationSec - expectedSec);
                    if (delta < bestDelta)
                    {
                        bestDelta = delta;
                        bestSeasonNumber = season.SeasonNumber;
                        bestEpisodeCount = epIdx;
                    }
                }
            }

            if (bestSeasonNumber == 0)
                return candidate;

            logger.LogInformation(
                "TV episode resolution for show {Id}: best match S{Season}, {Count} episode(s) on disc (delta={Delta:F1}s)",
                showId,
                bestSeasonNumber,
                bestEpisodeCount,
                bestDelta
            );

            // EpisodeNumber = 1 always (first episode on the disc);
            // SeasonNumber is the resolved season.
            return candidate with
            {
                SeasonNumber = bestSeasonNumber,
                EpisodeNumber = 1,
            };
        }
        catch (Exception ex)
        {
            logger.LogInformation(
                ex,
                "Episode resolution failed for show {Id}: {Message}",
                showId,
                ex.Message
            );
            return candidate;
        }
    }

    private static async Task<int?> FetchMovieRuntimeAsync(int movieId)
    {
        try
        {
            TmdbMovieClient client = new(movieId);
            TmdbMovieDetails? details = await client.Details();
            return details?.Runtime > 0 ? details.Runtime : null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<int?> FetchTvEpisodeRunTimeAsync(int showId)
    {
        try
        {
            TmdbTvClient client = new(showId);
            TmdbTvShowDetails? details = await client.Details();
            int? firstRuntime = details?.EpisodeRunTime?.FirstOrDefault();
            return firstRuntime is { } runtime and > 0 ? runtime : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Blends string similarity (60 %) with duration proximity (40 %).
    /// When <paramref name="discDurationSec"/> is zero or
    /// <paramref name="runtimeMin"/> is null, the weight shifts entirely
    /// to string similarity.
    /// </summary>
    internal static double BlendConfidence(
        string query,
        string candidate,
        int rank,
        int discDurationSec,
        int? runtimeMin
    )
    {
        double similarity = NormalizedSimilarity(query, candidate);
        double rankPenalty = Math.Max(0, 1.0 - (rank * 0.1));
        double strScore = similarity * rankPenalty;

        if (discDurationSec <= 0 || runtimeMin is null or 0)
            return Math.Round(strScore, 4);

        double runtimeSec = runtimeMin.Value * 60.0;
        double durDelta = Math.Abs(discDurationSec - runtimeSec);
        double durScore = 1.0 - Math.Clamp(durDelta / runtimeSec, 0.0, 1.0);

        double blended = (0.6 * strScore) + (0.4 * durScore);
        return Math.Round(blended, 4);
    }

    /// <summary>
    /// Volume labels come from the disc as e.g. <c>Avatar_Book_1_Disc_1</c>.
    /// Normalise to <c>Avatar Book 1</c> by replacing separators with spaces
    /// and stripping the trailing <c>disc N</c> token.
    /// </summary>
    internal static string NormalizeLabel(string label)
    {
        string spaced = label.Replace('_', ' ').Replace('.', ' ').Replace('-', ' ');
        spaced = MultiSpaceRegex().Replace(spaced, " ").Trim();
        spaced = DiscSuffixRegex().Replace(spaced, "").Trim();
        return spaced;
    }

    private static double NormalizedSimilarity(string a, string b)
    {
        HashSet<string> aTokens =
        [
            .. a.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries),
        ];
        HashSet<string> bTokens =
        [
            .. b.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries),
        ];
        if (aTokens.Count == 0 || bTokens.Count == 0)
            return 0;

        int intersection = aTokens.Intersect(bTokens).Count();
        int union = aTokens.Union(bTokens).Count();
        return union == 0 ? 0 : (double)intersection / union;
    }

    private static int? ParseYear(DateTime? date) => date?.Year;

    private static string? PosterUrl(string? posterPath)
    {
        if (string.IsNullOrEmpty(posterPath))
            return null;
        return $"{TmdbImageClient.ImageBaseUrl}w500{posterPath}";
    }

    private static string? BackdropUrl(string? backdropPath)
    {
        if (string.IsNullOrEmpty(backdropPath))
            return null;
        return $"{TmdbImageClient.ImageBaseUrl}w1280{backdropPath}";
    }

    private static DiscIdentification NeedsManual() =>
        new(
            Kind: MediaKind.Movie,
            Candidates: [],
            TopConfidence: 0,
            AutoApply: false,
            NeedsManualAssignment: true
        );

    [GeneratedRegex(@"\s+", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex MultiSpaceRegex();

    [GeneratedRegex(@"(\s+|^)disc\s*\d+\s*$", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex DiscSuffixRegex();
}
