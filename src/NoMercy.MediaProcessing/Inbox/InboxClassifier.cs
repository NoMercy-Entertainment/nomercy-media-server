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

using System.Text.RegularExpressions;
using MovieFileLibrary;
using NoMercy.Database.Models.Libraries;
using NoMercy.MediaProcessing.Shows;

namespace NoMercy.MediaProcessing.Inbox;

public sealed partial class InboxClassifier
{
    private static readonly string[] AudioExtensions = [".mp3", ".flac", ".opus", ".wav", ".m4a"];

    private static readonly string[] VideoExtensions =
    [
        ".mp4",
        ".mkv",
        ".avi",
        ".webm",
        ".mov",
        ".m3u8",
    ];

    // SxxExx e.g. S01E01, S1E1
    [GeneratedRegex(@"[Ss]\d{1,2}[Ee]\d{1,2}", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SeasonEpisodePattern();

    // NxNN e.g. 1x01, 2x12
    [GeneratedRegex(@"\b\d{1,2}x\d{2}\b", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex EpisodePrefixPattern();

    // "Season N" or "Season 01" in any path segment
    [GeneratedRegex(@"\bSeason\s*\d+\b", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SeasonFolderPattern();

    // "Title (Year)" movie shape — year in parens
    [GeneratedRegex(@"\((?:19|20)\d{2}\)", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex MovieYearPattern();

    // Fansub bracket at start of FILENAME: "[Group] Title - NNN ..."
    // Conservative: only flag an episodic (tv-shaped) file when the filename
    // starts with a bracket group AND has an absolute episode number as
    // " - NNN " (1-4 digits, for long-running shows like One Piece that
    // exceed 999 episodes). This signals "episodic", not "anime" — the
    // anime/tv split itself is decided by IMediaTypeClassifier (AniList/Jikan),
    // never by bracket-guessing, since normally-named anime files with no
    // fansub bracket were routing to the wrong library.
    [GeneratedRegex(@"^\[[^\]]+\].*\s-\s\d{1,4}\s", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex FansubAbsoluteEpPattern();

    [GeneratedRegex(@"\.NoMercy\.m3u8$", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex FinishedHlsMasterPattern();

    [GeneratedRegex(@"^(video_\d+x\d+(_.+)?|audio_[A-Za-z0-9_]+)$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex HlsLadderEntryPattern();

    [GeneratedRegex(
        @"\b(720p|1080p|2160p|4k|BluRay|WEB-DL|WEBRip|HDTV|x264|x265|HEVC|H\.?264|H\.?265|AAC|DTS|AC3)\b.*$",
        RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex QualityTagTail();

    [GeneratedRegex(@"\s*\((?:19|20)\d{2}\)\s*$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TrailingYearInParens();

    private readonly IInboxMetadataProbe _probe;
    private readonly IInboxAudioTagReader _tagReader;
    private readonly IMediaTypeClassifier _mediaTypeClassifier;

    public InboxClassifier(
        IInboxMetadataProbe probe,
        IInboxAudioTagReader tagReader,
        IMediaTypeClassifier mediaTypeClassifier
    )
    {
        _probe = probe;
        _tagReader = tagReader;
        _mediaTypeClassifier = mediaTypeClassifier;
    }

    // -----------------------------------------------------------------------
    // Pure static helpers — unit-testable with no deps
    // -----------------------------------------------------------------------

    public static string MediaFamilyOf(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();

        if (AudioExtensions.Contains(ext))
            return "music";

        if (VideoExtensions.Contains(ext))
            return "video";

        return "unknown";
    }

    public static string StructuralType(string path)
    {
        string filename = Path.GetFileNameWithoutExtension(path);

        if (FansubAbsoluteEpPattern().IsMatch(filename))
            return "tv";

        if (SeasonEpisodePattern().IsMatch(path))
            return "tv";

        if (EpisodePrefixPattern().IsMatch(path))
            return "tv";

        if (SeasonFolderPattern().IsMatch(path))
            return "tv";

        MovieDetector detector = new();
        MovieFile info = detector.GetInfo(path);

        if (info.IsSeries && (info.Season.HasValue || info.Episode.HasValue))
            return "tv";

        if (MovieYearPattern().IsMatch(path))
            return "movie";

        return "unknown";
    }

    public static bool IsFinishedHls(IEnumerable<string> siblingNames)
    {
        bool hasMaster = false;
        bool hasLadderEntry = false;

        foreach (string name in siblingNames)
        {
            if (FinishedHlsMasterPattern().IsMatch(name))
                hasMaster = true;

            if (HlsLadderEntryPattern().IsMatch(name))
                hasLadderEntry = true;
        }

        return hasMaster && hasLadderEntry;
    }

    // -----------------------------------------------------------------------
    // Full cascade
    // -----------------------------------------------------------------------

    public async Task<ClassificationResult> Classify(
        string path,
        Ulid driverId,
        CancellationToken ct
    )
    {
        string family = MediaFamilyOf(path);

        if (family == "music")
            return await ClassifyMusicAsync(path, driverId, ct);

        if (family == "video")
            return await ClassifyVideoAsync(path, ct);

        return new()
        {
            DetectedType = "unknown",
            Confidence = "low",
            Candidates = [],
        };
    }

    // -----------------------------------------------------------------------
    // Music branch
    // -----------------------------------------------------------------------

    private async Task<ClassificationResult> ClassifyMusicAsync(
        string path,
        Ulid driverId,
        CancellationToken ct
    )
    {
        InboxAudioTags? tags = await _tagReader.ReadAsync(path, driverId, ct);

        if (tags is null)
        {
            return new()
            {
                DetectedType = "music",
                Confidence = "low",
                Candidates = [],
            };
        }

        if (tags.MusicBrainzReleaseId.HasValue && tags.MusicBrainzReleaseId.Value != Guid.Empty)
        {
            CandidateMatch? candidate = await _probe.LookupMusicReleaseAsync(
                tags.MusicBrainzReleaseId.Value,
                ct
            );

            if (candidate is not null)
            {
                return new()
                {
                    DetectedType = "music",
                    Confidence = "high",
                    Candidates = [candidate],
                };
            }
        }

        // Tags present but no resolvable release id
        return new()
        {
            DetectedType = "music",
            Confidence = "medium",
            Candidates = [],
        };
    }

    // -----------------------------------------------------------------------
    // Video branch
    // -----------------------------------------------------------------------

    private async Task<ClassificationResult> ClassifyVideoAsync(string path, CancellationToken ct)
    {
        string structuralType = StructuralType(path);

        // Parse title and year from path for provider search
        MovieDetector detector = new();
        MovieFile info = detector.GetInfo(path);
        string title = ExtractTitle(path, info);

        if (string.IsNullOrWhiteSpace(title))
        {
            return new()
            {
                DetectedType = structuralType == "unknown" ? "unknown" : structuralType,
                Confidence = "low",
                Candidates = [],
            };
        }

        int? year = ExtractYear(path, info);

        // The structural signals only tell us the file is episodic
        // ("tv-shaped"); whether it belongs in the anime or tv library is
        // decided by the shared AniList/Jikan-backed classifier, never by filename
        // shape alone (e.g. a fansub bracket), so a normally-named anime
        // file with no bracket still lands in the right library.
        if (structuralType == "tv")
        {
            // A null verdict is an inconclusive lookup, not a "confirmed not
            // anime" — keep the structural "tv" guess rather than let an
            // AniList/Jikan hiccup silently stand in for a real answer.
            structuralType =
                await _mediaTypeClassifier.ClassifyAsync(title, year) ?? structuralType;
        }

        // Probe both movie and tv to handle ambiguous cases
        CandidateMatch[] movieHits = await _probe.SearchMoviesAsync(title, year, ct);
        CandidateMatch[] tvHits = await _probe.SearchTvAsync(title, year, ct);

        return FoldVideoResults(structuralType, year, movieHits, tvHits);
    }

    private static ClassificationResult FoldVideoResults(
        string structuralType,
        int? year,
        CandidateMatch[] movieHits,
        CandidateMatch[] tvHits
    )
    {
        bool hasStrongMovieHit = HasStrongHit(movieHits, year);
        bool hasStrongTvHit = HasStrongHit(tvHits, year);

        // Conflicting strong signals → low regardless of structural type
        if (hasStrongMovieHit && hasStrongTvHit)
        {
            CandidateMatch[] combined = [.. movieHits.Take(3), .. tvHits.Take(3)];
            return new()
            {
                DetectedType = structuralType == "unknown" ? "unknown" : structuralType,
                Confidence = "low",
                Candidates = RankCandidates(combined),
            };
        }

        // Anime structural type: conservative — send to review unless we also
        // have strong TV signal, since anime sometimes matches TV on TMDB.
        if (structuralType == "anime")
        {
            if (hasStrongTvHit && !hasStrongMovieHit)
            {
                return new()
                {
                    DetectedType = "anime",
                    Confidence = "medium",
                    Candidates = RankCandidates(tvHits),
                };
            }

            return new()
            {
                DetectedType = "anime",
                Confidence = "low",
                Candidates = RankCandidates([.. movieHits.Take(3), .. tvHits.Take(3)]),
            };
        }

        if (structuralType == "movie")
        {
            if (hasStrongMovieHit)
            {
                return new()
                {
                    DetectedType = "movie",
                    Confidence = "high",
                    Candidates = RankCandidates(movieHits),
                };
            }

            if (movieHits.Length > 0)
            {
                return new()
                {
                    DetectedType = "movie",
                    Confidence = "medium",
                    Candidates = RankCandidates(movieHits),
                };
            }

            return new()
            {
                DetectedType = "movie",
                Confidence = "low",
                Candidates = [],
            };
        }

        if (structuralType == "tv")
        {
            if (hasStrongTvHit)
            {
                return new()
                {
                    DetectedType = "tv",
                    Confidence = "high",
                    Candidates = RankCandidates(tvHits),
                };
            }

            if (tvHits.Length > 0)
            {
                return new()
                {
                    DetectedType = "tv",
                    Confidence = "medium",
                    Candidates = RankCandidates(tvHits),
                };
            }

            return new()
            {
                DetectedType = "tv",
                Confidence = "low",
                Candidates = [],
            };
        }

        // structuralType == "unknown"
        if (hasStrongMovieHit && !hasStrongTvHit)
        {
            return new()
            {
                DetectedType = "movie",
                Confidence = "medium",
                Candidates = RankCandidates(movieHits),
            };
        }

        if (hasStrongTvHit && !hasStrongMovieHit)
        {
            return new()
            {
                DetectedType = "tv",
                Confidence = "medium",
                Candidates = RankCandidates(tvHits),
            };
        }

        CandidateMatch[] allHits = [.. movieHits.Take(3), .. tvHits.Take(3)];
        return new()
        {
            DetectedType = "unknown",
            Confidence = "low",
            Candidates = RankCandidates(allHits),
        };
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    // A "strong" hit: top result with score >= 0.5 AND year matches (or no
    // year was available to check against)
    private static bool HasStrongHit(CandidateMatch[] hits, int? queriedYear)
    {
        if (hits.Length == 0)
            return false;

        CandidateMatch top = hits[0];

        if (top.Score < 0.50)
            return false;

        if (
            queriedYear.HasValue
            && top.Year.HasValue
            && Math.Abs(top.Year.Value - queriedYear.Value) > 1
        )
            return false;

        return true;
    }

    private static CandidateMatch[] RankCandidates(CandidateMatch[] candidates)
    {
        return [.. candidates.OrderByDescending(c => c.Score)];
    }

    private static string ExtractTitle(string path, MovieFile info)
    {
        if (!string.IsNullOrWhiteSpace(info.Title))
            return info.Title;

        // Fall back: filename without extension, strip common suffixes
        string name = Path.GetFileNameWithoutExtension(path);

        // Strip resolution / quality tags
        name = QualityTagTail().Replace(name, string.Empty).Trim();

        // Strip year in parens at end
        name = TrailingYearInParens().Replace(name, string.Empty).Trim();

        return name.Trim('.', ' ', '-', '_');
    }

    private static int? ExtractYear(string path, MovieFile info)
    {
        // MovieFile.Year is string? in MovieFileLibrary
        if (!string.IsNullOrWhiteSpace(info.Year) && int.TryParse(info.Year, out int parsed))
            return parsed;

        Match yearMatch = MovieYearPattern().Match(path);
        if (!yearMatch.Success)
            return null;

        string yearStr = yearMatch.Value.Trim('(', ')');
        return int.TryParse(yearStr, out int parsedFromPath) ? parsedFromPath : null;
    }
}
