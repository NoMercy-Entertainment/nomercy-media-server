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
using Microsoft.Extensions.Logging;
using NoMercy.Database;
using NoMercy.Database.Models.Media;
using NoMercy.Database.Models.TvShows;
using NoMercy.Encoder.Composition;
using NoMercy.Encoder.ContentAnalysis.Fingerprinting;
using NoMercy.Events;
using NoMercy.Events.Library;
using NoMercy.Storage;

namespace NoMercy.Encoder.Subscribers;

/// <summary>
/// Listens for <see cref="LibraryScanCompletedEvent"/> and runs chromaprint
/// fingerprinting + intro/outro detection for all episodes in each season
/// that belongs to the scanned library.
///
/// Skips episodes that already have a <c>Source="manual"</c> row of the same
/// type — manual annotations always win.
///
/// Opt-out: set <see cref="EncoderOptions.EnableIntroDetectSubscriber"/> = false.
/// </summary>
public class IntroDetectSubscriber : EventSubscriber
{
    private readonly IAudioFingerprinter _fingerprinter;
    private readonly IIntroDetector _introDetector;
    private readonly EncoderOptions _options;
    private readonly ILogger<IntroDetectSubscriber> _logger;
    private readonly IStorage _storage;
    private readonly IDbContextFactory<MediaContext> _contextFactory;

    // Fingerprint the first 3 minutes for intro detection.
    private static readonly FingerprintWindow IntroWindow = new(
        TimeSpan.Zero,
        TimeSpan.FromMinutes(3)
    );

    // Fingerprint the last 4 minutes for outro detection.
    private static readonly FingerprintWindow OutroWindow = new(
        TimeSpan.FromMinutes(-4),
        TimeSpan.FromMinutes(4)
    );

    public IntroDetectSubscriber(
        IEventBus eventBus,
        IAudioFingerprinter fingerprinter,
        IIntroDetector introDetector,
        EncoderOptions options,
        ILogger<IntroDetectSubscriber> logger,
        IStorage storage,
        IDbContextFactory<MediaContext> contextFactory
    )
    {
        _fingerprinter = fingerprinter;
        _introDetector = introDetector;
        _options = options;
        _logger = logger;
        _storage = storage;
        _contextFactory = contextFactory;

        Track(eventBus.Subscribe<LibraryScanCompletedEvent>(OnLibraryScanCompleted));
    }

    internal async Task OnLibraryScanCompleted(
        LibraryScanCompletedEvent @event,
        CancellationToken ct
    )
    {
        if (!_options.EnableIntroDetectSubscriber)
            return;

        // Load all TV seasons whose show belongs to this library.
        List<int> seasonIds;
        await using (MediaContext context = await _contextFactory.CreateDbContextAsync(ct))
        {
            seasonIds = await context
                .Seasons.AsNoTracking()
                .Where(s =>
                    context.LibraryTv.Any(lt =>
                        lt.LibraryId == @event.LibraryId && lt.TvId == s.TvId
                    )
                )
                .Select(s => s.Id)
                .ToListAsync(ct);
        }

        // Intro/outro detection is a TV-episode feature. A library with no TV
        // seasons — music, movies, or an empty library — has nothing to detect, so
        // skip it quietly instead of announcing a run that immediately does nothing.
        if (seasonIds.Count == 0)
        {
            _logger.LogDebug(
                "IntroDetect: no TV content in library {LibraryName} ({LibraryId}) — skipping",
                [@event.LibraryName, @event.LibraryId]
            );
            return;
        }

        _logger.LogInformation(
            "IntroDetect: starting for library {LibraryName} ({LibraryId}) — {SeasonCount} season(s)",
            [@event.LibraryName, @event.LibraryId, seasonIds.Count]
        );

        foreach (int seasonId in seasonIds)
        {
            if (ct.IsCancellationRequested)
                break;

            await ProcessSeasonAsync(seasonId, ct);
        }
    }

    private async Task ProcessSeasonAsync(int seasonId, CancellationToken ct)
    {
        List<Episode> episodes;
        HashSet<int> episodesWithIntro;
        HashSet<int> episodesWithOutro;

        await using (MediaContext context = await _contextFactory.CreateDbContextAsync(ct))
        {
            episodes = await context
                .Episodes.AsNoTracking()
                .Where(e => e.SeasonId == seasonId)
                .OrderBy(e => e.EpisodeNumber)
                .ToListAsync(ct);

            // Two-step to avoid SQLite APPLY: fetch episode IDs with existing segments first.
            List<int> episodeIds = [.. episodes.Select(e => e.Id)];

            // Every existing segment regardless of Source — a prior detector
            // run's rows must block re-insertion on a rescan just as much as a
            // manual one, or a repeated LibraryScanCompletedEvent duplicates
            // every segment row on each pass.
            List<ContentSegment> existingSegments = await context
                .ContentSegments.AsNoTracking()
                .Where(cs => cs.EpisodeId != null && episodeIds.Contains(cs.EpisodeId!.Value))
                .ToListAsync(ct);

            episodesWithIntro =
            [
                .. existingSegments
                    .Where(cs => cs.SegmentType == ContentSegmentType.Intro)
                    .Select(cs => cs.EpisodeId!.Value),
            ];

            episodesWithOutro =
            [
                .. existingSegments
                    .Where(cs => cs.SegmentType == ContentSegmentType.Outro)
                    .Select(cs => cs.EpisodeId!.Value),
            ];
        }

        if (episodes.Count < 2)
        {
            _logger.LogDebug(
                "IntroDetect: season {SeasonId} has fewer than 2 episodes — skipping",
                seasonId
            );
            return;
        }

        // Resolve source file paths for each episode.
        List<(Episode Episode, string FilePath)> episodeFiles = await ResolveFilePathsAsync(
            episodes,
            ct
        );

        if (episodeFiles.Count < 2)
            return;

        // Fingerprint intro windows.
        bool needIntro = episodeFiles.Any(ef => !episodesWithIntro.Contains(ef.Episode.Id));
        bool needOutro = episodeFiles.Any(ef => !episodesWithOutro.Contains(ef.Episode.Id));

        if (!needIntro && !needOutro)
        {
            _logger.LogDebug(
                "IntroDetect: all episodes in season {SeasonId} already have manual segments",
                seasonId
            );
            return;
        }

        List<ContentSegment> newSegments = [];

        if (needIntro)
        {
            List<(Episode Episode, AudioFingerprint Fp)> introFingerprints = await FingerprintAsync(
                episodeFiles,
                IntroWindow,
                ct
            );

            if (introFingerprints.Count >= 2)
            {
                IntroMarker? marker = _introDetector.DetectIntro([
                    .. introFingerprints.Select(x => x.Fp),
                ]);

                if (marker is not null)
                {
                    foreach ((Episode episode, _) in introFingerprints)
                    {
                        if (episodesWithIntro.Contains(episode.Id))
                            continue;

                        newSegments.Add(
                            new()
                            {
                                EpisodeId = episode.Id,
                                SegmentType = ContentSegmentType.Intro,
                                StartSeconds = marker.Start.TotalSeconds,
                                EndSeconds = marker.End.TotalSeconds,
                                Confidence = marker.Confidence,
                                Source = "detector",
                            }
                        );
                    }
                }
            }
        }

        if (needOutro)
        {
            List<(Episode Episode, AudioFingerprint Fp)> outroFingerprints = await FingerprintAsync(
                episodeFiles,
                OutroWindow,
                ct
            );

            if (outroFingerprints.Count >= 2)
            {
                IntroMarker? marker = _introDetector.DetectOutro([
                    .. outroFingerprints.Select(x => x.Fp),
                ]);

                if (marker is not null)
                {
                    foreach ((Episode episode, _) in outroFingerprints)
                    {
                        if (episodesWithOutro.Contains(episode.Id))
                            continue;

                        newSegments.Add(
                            new()
                            {
                                EpisodeId = episode.Id,
                                SegmentType = ContentSegmentType.Outro,
                                StartSeconds = marker.Start.TotalSeconds,
                                EndSeconds = marker.End.TotalSeconds,
                                Confidence = marker.Confidence,
                                Source = "detector",
                            }
                        );
                    }
                }
            }
        }

        if (newSegments.Count == 0)
            return;

        await using (MediaContext context = await _contextFactory.CreateDbContextAsync(ct))
        {
            context.ContentSegments.AddRange(newSegments);
            await context.SaveChangesAsync(ct);
        }

        _logger.LogInformation(
            "IntroDetect: persisted {Count} segment(s) for season {SeasonId}",
            [newSegments.Count, seasonId]
        );
    }

    private async Task<List<(Episode Episode, string FilePath)>> ResolveFilePathsAsync(
        List<Episode> episodes,
        CancellationToken ct
    )
    {
        List<int> episodeIds = [.. episodes.Select(e => e.Id)];

        List<(int EpisodeId, string HostFolder, string Filename)> fileRows;
        await using (MediaContext context = await _contextFactory.CreateDbContextAsync(ct))
        {
            fileRows = await context
                .VideoFiles.AsNoTracking()
                .Where(vf => vf.EpisodeId != null && episodeIds.Contains(vf.EpisodeId!.Value))
                .Select(vf => new ValueTuple<int, string, string>(
                    vf.EpisodeId!.Value,
                    vf.HostFolder,
                    vf.Filename
                ))
                .ToListAsync(ct);
        }

        Dictionary<int, string> fileByEpisode = fileRows.ToDictionary(
            r => r.EpisodeId,
            r => r.HostFolder + r.Filename
        );

        return
        [
            .. episodes
                .Where(e => fileByEpisode.ContainsKey(e.Id))
                .Select(e => (e, fileByEpisode[e.Id])),
        ];
    }

    private async Task<List<(Episode Episode, AudioFingerprint Fp)>> FingerprintAsync(
        List<(Episode Episode, string FilePath)> episodeFiles,
        FingerprintWindow window,
        CancellationToken ct
    )
    {
        List<(Episode Episode, AudioFingerprint Fp)> results = [];

        foreach ((Episode episode, string filePath) in episodeFiles)
        {
            if (ct.IsCancellationRequested)
                break;

            if (!_storage.Exists(filePath))
            {
                _logger.LogDebug(
                    "IntroDetect: file not found for episode {EpisodeId}: {FilePath}",
                    [episode.Id, filePath]
                );
                continue;
            }

            try
            {
                AudioFingerprint fp = await _fingerprinter.FingerprintAsync(filePath, window, ct);
                results.Add((episode, fp));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "IntroDetect: fingerprinting failed for episode {EpisodeId}",
                    episode.Id
                );
            }
        }

        return results;
    }
}
