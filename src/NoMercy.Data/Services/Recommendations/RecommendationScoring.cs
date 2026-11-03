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

using NoMercy.Data.Repositories;
using NoMercy.NmSystem.Domain;

namespace NoMercy.Data.Services.Recommendations;

public record UserAffinityProfile
{
    public Dictionary<int, double> GenreAffinity { get; init; } = new();
    public Dictionary<SourceKey, UserAffinitySourceDto> SourceItems { get; init; } = new();
    public HashSet<int> FavoritedMovieIds { get; init; } = [];
    public HashSet<int> FavoritedTvIds { get; init; } = [];
}

/// <summary>How recommendation candidates are merged, scored and picked for a user.</summary>
public static class RecommendationScoring
{
    /// <summary>
    /// The user's taste: genre weights from what they rated, finished and favorited,
    /// normalised to 0–1, plus their watched titles and favorites.
    /// </summary>
    public static UserAffinityProfile BuildProfile(IEnumerable<UserAffinitySourceDto> allSources)
    {
        Dictionary<int, double> genreScores = new();
        Dictionary<SourceKey, UserAffinitySourceDto> sourceMap = new();
        HashSet<int> favMovies = [];
        HashSet<int> favTvs = [];

        foreach (UserAffinitySourceDto src in allSources)
        {
            sourceMap[new SourceKey(src.MediaType, src.ItemId)] = src;
            if (src.IsFavorited)
            {
                if (src.MediaType == MediaTypes.MovieMediaType)
                    favMovies.Add(src.ItemId);
                else
                    favTvs.Add(src.ItemId);
            }

            double weight = 1.0;
            if (src.Rating.HasValue)
                weight += (src.Rating.Value - 5) / 5.0;
            if (
                src is { TimeWatched: > 0, Duration: > 0 }
                && (double)src.TimeWatched / src.Duration.Value > 0.8
            )
                weight += 0.5;
            if (src.IsFavorited)
                weight += 1.0;

            foreach (int genreId in src.GenreIds)
            {
                genreScores.TryAdd(genreId, 0);
                genreScores[genreId] += weight;
            }
        }

        // Normalize genre scores to 0–1 range
        double maxGenre = genreScores.Values.DefaultIfEmpty(1).Max();
        Dictionary<int, double> genreAffinity = genreScores.ToDictionary(
            kv => kv.Key,
            kv => kv.Value / maxGenre
        );

        return new()
        {
            GenreAffinity = genreAffinity,
            SourceItems = sourceMap,
            FavoritedMovieIds = favMovies,
            FavoritedTvIds = favTvs,
        };
    }

    /// <summary>
    /// Keyword ids per source title, split by media type, for the titles the user
    /// favorited, rated 6 or higher, or watched past half.
    /// </summary>
    public static (
        Dictionary<SourceKey, List<int>> Movie,
        Dictionary<SourceKey, List<int>> Tv,
        Dictionary<SourceKey, List<int>> Anime
    ) HighSignalKeywordMaps(UserAffinityProfile profile)
    {
        Dictionary<SourceKey, List<int>> movie = new();
        Dictionary<SourceKey, List<int>> tv = new();
        Dictionary<SourceKey, List<int>> anime = new();

        foreach ((SourceKey key, UserAffinitySourceDto src) in profile.SourceItems)
        {
            if (src.KeywordIds.Count == 0)
                continue;

            bool isHighSignal =
                src.IsFavorited
                || src.Rating is >= 6
                || (
                    src is { TimeWatched: > 0, Duration: > 0 }
                    && (double)src.TimeWatched / src.Duration.Value > 0.5
                );
            if (!isHighSignal)
                continue;

            if (src.MediaType == MediaTypes.MovieMediaType)
                movie[key] = src.KeywordIds;
            else if (src.MediaType == MediaTypes.AnimeMediaType)
                anime[key] = src.KeywordIds;
            else
                tv[key] = src.KeywordIds;
        }

        return (movie, tv, anime);
    }

    public static List<RecommendationCandidateDto> MergeCandidates(
        params List<RecommendationCandidateDto>[] candidateLists
    )
    {
        Dictionary<string, RecommendationCandidateDto> merged = new();

        foreach (List<RecommendationCandidateDto> list in candidateLists)
        {
            foreach (RecommendationCandidateDto candidate in list)
            {
                string key = $"{candidate.MediaType}:{candidate.MediaId}";
                if (merged.TryGetValue(key, out RecommendationCandidateDto? existing))
                {
                    existing.SourceCount += candidate.SourceCount;
                    existing.SourceIds = existing.SourceIds.Union(candidate.SourceIds).ToList();
                }
                else
                {
                    merged[key] = candidate;
                }
            }
        }

        return merged.Values.ToList();
    }

    public static double ScoreCandidate(
        RecommendationCandidateDto candidate,
        UserAffinityProfile profile,
        Dictionary<SourceKey, List<int>> sourceGenreMap
    )
    {
        double score = 0.0;

        // 1. Frequency: use distinct source families instead of raw count to prevent franchise flooding
        //    (e.g., 10 "Tom and Jerry" movies should count as ~1 family, not 10 separate signals)
        int effectiveSourceCount = CountDistinctSourceFamilies(candidate.SourceIds, profile);
        score += Math.Min(effectiveSourceCount, 5) / 5.0 * 3.0;

        // 2. Source rating: average user rating of source items
        List<double> sourceRatings = candidate
            .SourceIds.Where(id =>
                profile.SourceItems.ContainsKey(id) && profile.SourceItems[id].Rating.HasValue
            )
            .Select(id => (double)profile.SourceItems[id].Rating!.Value)
            .ToList();
        if (sourceRatings.Count > 0)
            score += sourceRatings.Average() / 10.0 * 2.0;

        // 3. Source watch completion
        List<double> completions = candidate
            .SourceIds.Where(id => profile.SourceItems.ContainsKey(id))
            .Select(id =>
            {
                UserAffinitySourceDto src = profile.SourceItems[id];
                if (src is { TimeWatched: > 0, Duration: > 0 })
                    return Math.Min((double)src.TimeWatched / src.Duration.Value, 1.0);
                return 0.0;
            })
            .ToList();
        if (completions.Count > 0)
            score += completions.Average() * 1.5;

        // 4. Genre match via source items' genres as proxy
        List<int> candidateGenreIds = candidate
            .SourceIds.Where(id => sourceGenreMap.ContainsKey(id))
            .SelectMany(id => sourceGenreMap[id])
            .Distinct()
            .ToList();
        if (candidateGenreIds.Count > 0)
        {
            double genreMatch = candidateGenreIds
                .Where(gId => profile.GenreAffinity.ContainsKey(gId))
                .Sum(gId => profile.GenreAffinity[gId]);
            score += genreMatch / candidateGenreIds.Count * 2.5;
        }

        // 5. Favorite source bonus — each source is looked up in the set of its own media type
        bool hasFavoritedSource = candidate.SourceIds.Any(id =>
            id.MediaType == MediaTypes.MovieMediaType
                ? profile.FavoritedMovieIds.Contains(id.ItemId)
                : profile.FavoritedTvIds.Contains(id.ItemId)
        );
        if (hasFavoritedSource)
            score += 1.0;

        return score;
    }

    /// <summary>
    /// Clusters source items by title family to prevent franchise flooding.
    /// Sources sharing a long common prefix (e.g., "Tom and Jerry: X", "Tom and Jerry: Y")
    /// are counted as one family instead of inflating the frequency score.
    /// </summary>
    public static int CountDistinctSourceFamilies(
        List<SourceKey> sourceIds,
        UserAffinityProfile profile
    )
    {
        List<string> titles = sourceIds
            .Where(id => profile.SourceItems.ContainsKey(id))
            .Select(id => profile.SourceItems[id].Title)
            .Where(t => !string.IsNullOrEmpty(t))
            .ToList();

        if (titles.Count <= 1)
            return titles.Count;

        // Cluster by shared prefix: if two titles share the first 60%+ characters of the shorter one,
        // they're in the same family (e.g., "Tom and Jerry: The Movie" and "Tom and Jerry: Willy Wonka")
        List<string> families = [];
        foreach (string title in titles)
            TitleFamily.Assign(title, families);

        return families.Count;
    }

    /// <summary>
    /// Guarantees a minimum floor of (take / typeCount) results per media type,
    /// then fills remaining slots with the highest-scored items from any type.
    /// </summary>
    public static List<T> SelectWithDiversity<T>(
        List<T> scored,
        int take,
        Func<T, string> type,
        Func<T, double> score
    )
    {
        Dictionary<string, Queue<T>> byType = scored
            .GroupBy(type)
            .ToDictionary(g => g.Key, g => new Queue<T>(g.OrderByDescending(score)));

        int typeCount = byType.Count;
        if (typeCount <= 1)
            return scored.OrderByDescending(score).Take(take).ToList();

        // Give each type a guaranteed floor of (take / typeCount) slots
        int floorSlots = take / typeCount;
        List<T> result = [];
        foreach (Queue<T> queue in byType.Values)
        {
            int toTake = Math.Min(floorSlots, queue.Count);
            for (int i = 0; i < toTake; i++)
                result.Add(queue.Dequeue());
        }

        // Fill remaining slots with best-scored items from any type
        int remaining = take - result.Count;
        if (remaining > 0)
        {
            List<T> overflow = byType
                .Values.SelectMany(q => q)
                .OrderByDescending(score)
                .Take(remaining)
                .ToList();
            result.AddRange(overflow);
        }

        return result.OrderByDescending(score).ToList();
    }
}
