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

using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NoMercy.Database;
using NoMercy.Database.Models.Media;
using NoMercy.Database.Models.TvShows;
using NoMercy.Encoder.ContentAnalysis.Fingerprinting;
using NoMercy.Events;
using NoMercy.Events.Encoding;
using NoMercy.Storage;

namespace NoMercy.MediaProcessing.EventHandlers;

/// <summary>
/// Wires <see cref="EncodingCompletedEvent"/> to the chromaprint-based
/// intro/outro detector and persists the results as
/// <see cref="ContentSegment"/> rows so the player can render "Skip Intro"
/// buttons. Runs in the background — fingerprinting is ffmpeg-heavy, so the
/// subscriber queues work on a <see cref="Task.Run"/> to keep the event
/// bus callback cheap.
///
/// Detection only fires once a season has at least <see cref="MinEpisodes"/>
/// encoded episodes. Any fewer and there isn't enough cross-episode data
/// to pick out a shared opening/closing theme reliably.
/// </summary>
public class IntroDetectionSubscriber(
    IEventBus eventBus,
    IServiceScopeFactory scopeFactory,
    ILogger<IntroDetectionSubscriber> logger,
    IStorage storage
) : IHostedService
{
    private const int MinEpisodes = 3;
    private static readonly TimeSpan IntroScanWindow = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan OutroScanWindow = TimeSpan.FromMinutes(3);

    private readonly ConcurrentDictionary<int, SeasonScanState> _seasonScans = new();
    private readonly List<IDisposable> _subscriptions = [];

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscriptions.Add(
            eventBus.Subscribe<EncodingCompletedEvent>(
                (evt, ct) =>
                {
                    _ = Task.Run(() => OnEncodingCompletedAsync(evt, ct), ct);
                    return Task.CompletedTask;
                }
            )
        );

        logger.LogInformation("Intro detection subscriber active");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (IDisposable subscription in _subscriptions)
        {
            try
            {
                subscription.Dispose();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not dispose intro detection subscription");
            }
        }
        _subscriptions.Clear();
        return Task.CompletedTask;
    }

    private async Task OnEncodingCompletedAsync(EncodingCompletedEvent evt, CancellationToken ct)
    {
        try
        {
            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
            MediaContext context = scope.ServiceProvider.GetRequiredService<MediaContext>();

            Episode? episode = await context
                .Episodes.AsNoTracking()
                .Include(e => e.VideoFiles)
                .FirstOrDefaultAsync(e => e.Id == evt.JobId, ct);

            if (episode is null)
                return; // Not an episode — movies don't have cross-episode intro matching yet.

            SeasonScanState state = _seasonScans.GetOrAdd(episode.SeasonId, _ => new());
            lock (state.Sync)
            {
                if (state.Running)
                {
                    state.Dirty = true;
                    return;
                }

                state.Running = true;
            }

            try
            {
                while (true)
                {
                    await DetectAndPersistForSeasonAsync(
                        scope.ServiceProvider,
                        episode.SeasonId,
                        ct
                    );

                    lock (state.Sync)
                    {
                        if (!state.Dirty)
                        {
                            state.Running = false;
                            return;
                        }

                        state.Dirty = false;
                    }
                }
            }
            finally
            {
                lock (state.Sync)
                {
                    state.Running = false;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Intro detection failed for job {JobId}", evt.JobId);
        }
    }

    private async Task DetectAndPersistForSeasonAsync(
        IServiceProvider services,
        int seasonId,
        CancellationToken ct
    )
    {
        MediaContext context = services.GetRequiredService<MediaContext>();
        List<Episode> encodedEpisodes = await context
            .Episodes.AsNoTracking()
            .Include(e => e.VideoFiles)
            .Where(e => e.SeasonId == seasonId && e.VideoFiles.Count > 0)
            .OrderBy(e => e.EpisodeNumber)
            .ToListAsync(ct);

        if (encodedEpisodes.Count < MinEpisodes)
        {
            logger.LogDebug(
                "Season {SeasonId} only has {Count} encoded episodes — skipping (need {Min})",
                [seasonId, encodedEpisodes.Count, MinEpisodes]
            );
            return;
        }

        IAudioFingerprinter fingerprinter = services.GetRequiredService<IAudioFingerprinter>();
        IIntroDetector detector = services.GetRequiredService<IIntroDetector>();

        Dictionary<int, (AudioFingerprint Intro, AudioFingerprint Outro)> fingerprints = new();

        foreach (Episode episode in encodedEpisodes)
        {
            ct.ThrowIfCancellationRequested();

            string? inputPath = ResolveEpisodeInputPath(episode);
            if (inputPath is null)
                continue;

            try
            {
                AudioFingerprint introPrint = await fingerprinter.FingerprintAsync(
                    inputPath,
                    new(TimeSpan.Zero, IntroScanWindow),
                    ct
                );

                TimeSpan sourceDuration = ParseDuration(episode.VideoFiles.FirstOrDefault());
                TimeSpan outroStart =
                    sourceDuration > OutroScanWindow
                        ? sourceDuration - OutroScanWindow
                        : TimeSpan.Zero;

                AudioFingerprint outroPrint = await fingerprinter.FingerprintAsync(
                    inputPath,
                    new(outroStart, OutroScanWindow),
                    ct
                );

                fingerprints[episode.Id] = (introPrint, outroPrint);
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Could not fingerprint episode {EpisodeId} — excluding from season scan",
                    episode.Id
                );
            }
        }

        if (fingerprints.Count < MinEpisodes)
            return;

        IReadOnlyList<AudioFingerprint> intros = [.. fingerprints.Values.Select(f => f.Intro)];
        IReadOnlyList<AudioFingerprint> outros = [.. fingerprints.Values.Select(f => f.Outro)];

        IntroMarker? introMarker = detector.DetectIntro(intros);
        IntroMarker? outroMarker = detector.DetectOutro(outros);

        foreach (int episodeId in fingerprints.Keys)
        {
            List<ContentSegment> segments = [];
            if (introMarker is not null)
            {
                segments.Add(
                    new()
                    {
                        SegmentType = ContentSegmentType.Intro,
                        StartSeconds = introMarker.Start.TotalSeconds,
                        EndSeconds = introMarker.End.TotalSeconds,
                        Confidence = introMarker.Confidence,
                    }
                );
            }
            if (outroMarker is not null)
            {
                segments.Add(
                    new()
                    {
                        SegmentType = ContentSegmentType.Outro,
                        StartSeconds = outroMarker.Start.TotalSeconds,
                        EndSeconds = outroMarker.End.TotalSeconds,
                        Confidence = outroMarker.Confidence,
                    }
                );
            }

            if (segments.Count > 0)
                await ReplaceDetectorSegmentsAsync(context, episodeId, segments, ct);
        }

        logger.LogInformation(
            "Intro detection completed for season {SeasonId}: intro={HasIntro} outro={HasOutro} across {Count} episodes",
            [seasonId, introMarker is not null, outroMarker is not null, fingerprints.Count]
        );
    }

    /// <summary>
    /// Removes previously-written detector rows for this episode and writes
    /// the fresh set. Manual edits (Source != "detector") are left alone so
    /// user corrections survive re-detection.
    /// </summary>
    private static async Task ReplaceDetectorSegmentsAsync(
        MediaContext context,
        int episodeId,
        List<ContentSegment> newSegments,
        CancellationToken ct
    )
    {
        List<ContentSegment> old = await context
            .ContentSegments.Where(s => s.EpisodeId == episodeId && s.Source == "detector")
            .ToListAsync(ct);

        context.ContentSegments.RemoveRange(old);

        foreach (ContentSegment seg in newSegments)
        {
            seg.EpisodeId = episodeId;
            seg.MovieId = null;
            seg.Source = "detector";
            seg.CreatedAt = DateTime.UtcNow;
            seg.UpdatedAt = seg.CreatedAt;
            context.ContentSegments.Add(seg);
        }

        await context.SaveChangesAsync(ct);
    }

    private string? ResolveEpisodeInputPath(Episode episode)
    {
        // The subscriber fingerprints the ORIGINAL source (not HLS output)
        // so the timestamps it detects are meaningful against the full
        // timeline the player sees. Use the first non-empty VideoFile.
        foreach (VideoFile file in episode.VideoFiles)
        {
            if (
                string.IsNullOrWhiteSpace(file.HostFolder)
                || string.IsNullOrWhiteSpace(file.Filename)
            )
                continue;

            string path = storage.CombinePath(file.HostFolder, file.Filename);
            if (storage.Exists(path))
                return path;
        }

        return null;
    }

    private static TimeSpan ParseDuration(VideoFile? file)
    {
        if (file is null || string.IsNullOrWhiteSpace(file.Duration))
            return TimeSpan.Zero;

        // VideoFile.Duration is stored as "HH:MM:SS" in the DB.
        if (TimeSpan.TryParse(file.Duration, out TimeSpan parsed))
            return parsed;

        return TimeSpan.Zero;
    }

    private sealed class SeasonScanState
    {
        public object Sync { get; } = new();
        public bool Running { get; set; }
        public bool Dirty { get; set; }
    }
}
