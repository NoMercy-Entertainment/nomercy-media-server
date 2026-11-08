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
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Media;
using NoMercy.Events;
using NoMercy.Events.Library;
using NoMercy.MediaProcessing.Jobs.MediaJobs;
using NoMercy.Storage;
using NoMercyQueue.Core.Interfaces;

namespace NoMercy.MediaProcessing.EventHandlers;

/// <summary>
/// Opt-in video-archiver workflow: when a newly-scanned movie or episode
/// lands in a library folder that has an <c>EncodingPresetFolder</c> (V2)
/// link, queue a <c>VideoEncodeJob</c> for each video file that hasn't been
/// encoded yet — but only when the owning <see cref="Library"/> has
/// <see cref="Library.AutoEncodeOnScan"/> turned on and a
/// <see cref="Library.EncodePresetId"/> assigned.
///
/// The flag defaults OFF per library. A preset link on a folder means "this
/// is the preset to use when I encode", not "re-encode everything the
/// moment it is scanned"; conflating the two silently doubled operators'
/// disk usage by writing a full HLS ladder alongside every original.
/// Auto-encode is a deliberate per-library dashboard choice, so an existing
/// preset assignment never triggers an encode on its own — and when it does
/// trigger, only <see cref="Library.EncodePresetId"/> runs, not every
/// preset linked to the folder.
///
/// The folder-selection gate reads the same V2 <c>EncodingPresetFolders</c>
/// table <see cref="NoMercy.MediaProcessing.Jobs.MediaJobs.VideoEncodeJob"/>
/// resolves its presets from, so a folder that VideoEncodeJob would actually
/// encode is exactly the folder this subscriber dispatches for.
///
/// Skips files whose expected encoded output directory already exists so
/// rescans don't re-encode.
/// </summary>
public class AutoEncodeSubscriber(
    IEventBus eventBus,
    ILogger<AutoEncodeSubscriber> logger,
    IStorage storage,
    IDbContextFactory<MediaContext> contextFactory,
    IJobDispatcher dispatcher,
    TimeSpan? batchQuietPeriod = null
) : IHostedService
{
    private readonly List<IDisposable> _subscriptions = [];

    private static readonly TimeSpan DefaultQuietPeriod = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MaxHold = TimeSpan.FromMinutes(2);

    private readonly Lock _holdLock = new();
    private readonly List<(string SortKey, VideoEncodeJob Job)> _held = [];
    private Timer? _flushTimer;
    private DateTime _firstHeldAt;

    // Scans of one season finish in worker order, not episode order. Holding the
    // jobs briefly and queueing them sorted keeps the queue's Priority, Id order
    // equal to episode order. A zero quiet period dispatches at once.
    private void Submit(VideoEncodeJob job, string sortKey)
    {
        TimeSpan quiet = batchQuietPeriod ?? DefaultQuietPeriod;
        if (quiet <= TimeSpan.Zero)
        {
            dispatcher.Dispatch(job, job.QueueName, job.Priority);
            return;
        }

        lock (_holdLock)
        {
            if (_held.Count == 0)
                _firstHeldAt = DateTime.UtcNow;
            _held.Add((sortKey, job));

            TimeSpan untilMaxHold = _firstHeldAt + MaxHold - DateTime.UtcNow;
            TimeSpan due = untilMaxHold < quiet ? untilMaxHold : quiet;
            _flushTimer ??= new(_ => FlushHeld(), null, Timeout.Infinite, Timeout.Infinite);
            _flushTimer.Change(due < TimeSpan.Zero ? TimeSpan.Zero : due, Timeout.InfiniteTimeSpan);
        }
    }

    private void FlushHeld()
    {
        try
        {
            FlushAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Auto-encode could not queue its held jobs");
        }
    }

    public Task FlushAsync()
    {
        List<(string SortKey, VideoEncodeJob Job)> batch;
        lock (_holdLock)
        {
            batch = [.. _held];
            _held.Clear();
            _flushTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        }

        foreach ((_, VideoEncodeJob job) in batch.OrderBy(e => e.SortKey, NaturalStringComparer.Instance))
            dispatcher.Dispatch(job, job.QueueName, job.Priority);

        if (batch.Count > 0)
            logger.LogInformation(
                "Auto-encode queued {Count} held VideoEncodeJob(s) in episode order",
                batch.Count
            );

        return Task.CompletedTask;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscriptions.Add(
            eventBus.Subscribe<MediaFilesScannedEvent>((evt, ct) => HandleAsync(evt, ct))
        );
        logger.LogInformation("Auto-encode subscriber active");
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
                logger.LogWarning(ex, "Could not dispose auto-encode subscription");
            }
        }
        _subscriptions.Clear();
        _flushTimer?.Dispose();
        _flushTimer = null;
        return FlushAsync();
    }

    private async Task HandleAsync(MediaFilesScannedEvent evt, CancellationToken ct)
    {
        try
        {
            await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);

            Library? library = await context
                .Libraries.AsNoTracking()
                .FirstOrDefaultAsync(l => l.Id == evt.LibraryId, ct);

            if (library is null)
            {
                logger.LogDebug(
                    "Auto-encode skipped: library {LibraryId} not found",
                    evt.LibraryId
                );
                return;
            }

            if (!library.AutoEncodeOnScan)
            {
                logger.LogDebug(
                    "Auto-encode-on-scan is off for library {LibraryId}; scan skipped",
                    evt.LibraryId
                );
                return;
            }

            if (library.EncodePresetId is null)
            {
                logger.LogDebug(
                    "Auto-encode-on-scan is on for library {LibraryId} but no encoding preset is assigned; scan skipped",
                    evt.LibraryId
                );
                return;
            }

            // All folders in this library that have a V2 encoding preset
            // attached — same table VideoEncodeJob resolves its presets from.
            List<Folder> folders = await context
                .Folders.Include(f => f.EncodingPresetFolders)
                    .ThenInclude(link => link.Preset)
                .Include(f => f.FolderLibraries)
                .Where(f =>
                    f.FolderLibraries.Any(fl => fl.LibraryId == evt.LibraryId)
                    && f.EncodingPresetFolders.Any()
                )
                .ToListAsync(ct);

            if (folders.Count == 0)
            {
                logger.LogDebug(
                    "No folders with encoding presets in library {LibraryId}; auto-encode skipped",
                    evt.LibraryId
                );
                return;
            }

            // Video files for this media id — match on movie id OR episode id.
            // Multiple sources per media (NFS + S3 + ...) is expected, so dedupe
            // by Filename and pick the best driver to read from. The encoder
            // publishes its output to every configured destination from a single
            // run, so we never need to encode the same content twice.
            List<VideoFile> videoFiles = await context
                .VideoFiles.Where(vf => vf.MovieId == evt.MediaId || vf.EpisodeId == evt.MediaId)
                .ToListAsync(ct);

            if (videoFiles.Count == 0)
            {
                logger.LogDebug(
                    "No video files yet for media {MediaId} — auto-encode skipped",
                    evt.MediaId
                );
                return;
            }

            int dispatched = 0;

            // Pre-load drivers so we can rank sources by storage type
            // (local > nfs/smb > webdav > r2/s3) without N+1 queries.
            Dictionary<Ulid, string> driverTypeById = await context
                .Drivers.AsNoTracking()
                .ToDictionaryAsync(d => d.Id, d => d.Type, ct);

            foreach (
                (VideoFile bestSource, Folder folder) in SelectSourcesToEncode(
                    videoFiles,
                    folders,
                    driverTypeById
                )
            )
            {
                if (!string.IsNullOrEmpty(bestSource.Folder) && IsAlreadyEncoded(bestSource))
                    continue;

                string filePath =
                    bestSource.HostFolder.TrimEnd('/') + "/" + bestSource.Filename.TrimStart('/');
                VideoEncodeJob job = new()
                {
                    LibraryId = evt.LibraryId,
                    FolderId = folder.Id,
                    Id = evt.MediaId.ToString(),
                    InputFile = filePath,
                    PresetId = library.EncodePresetId.Value,
                };
                Submit(job, bestSource.HostFolder.TrimEnd('/') + "/" + bestSource.Filename);
                dispatched++;
            }

            if (dispatched > 0)
            {
                logger.LogInformation(
                    "Auto-encode dispatched {Count} VideoEncodeJob(s) for media {MediaId}",
                    dispatched,
                    evt.MediaId
                );
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Auto-encode handler failed for media {MediaId}", evt.MediaId);
        }
    }

    /// <summary>
    /// Pure decision function: given the VideoFiles for one media and the
    /// available folders+driver mapping, return one (source, folder) pair per
    /// distinct filename — the best source ranked by driver type. Same media
    /// content stored on NFS + S3 gets exactly one encode dispatched, against
    /// the fastest source. Internal so the test project can pin every branch
    /// without spinning up a DB.
    /// </summary>
    internal static IEnumerable<(VideoFile File, Folder Folder)> SelectSourcesToEncode(
        IReadOnlyList<VideoFile> videoFiles,
        IReadOnlyList<Folder> folders,
        Dictionary<Ulid, string> driverTypeById
    )
    {
        return videoFiles
            .GroupBy(vf => vf.Filename)
            .Select(group =>
                group
                    .Select(file => new
                    {
                        File = file,
                        Folder = folders.FirstOrDefault(f =>
                            !string.IsNullOrEmpty(file.HostFolder)
                            && file.HostFolder.StartsWith(
                                f.Path,
                                StringComparison.OrdinalIgnoreCase
                            )
                        ),
                    })
                    .Where(x => x.Folder is not null)
                    .OrderBy(x => DriverPreference(driverTypeById, x.Folder!.DriverId))
                    .Select(x => (File: x.File, Folder: x.Folder!))
                    .FirstOrDefault()
            )
            .Where(pair => pair.File is not null && pair.Folder is not null);
    }

    // Lower number = preferred. Picks the lowest-latency / cheapest source
    // when the same media is mounted on multiple drivers.
    internal static int DriverPreference(Dictionary<Ulid, string> typeById, Ulid driverId)
    {
        if (!typeById.TryGetValue(driverId, out string? type))
            return 99;

        return type.ToLowerInvariant() switch
        {
            "local" => 0,
            "nfs" or "smb" or "cifs" => 1,
            "webdav" => 2,
            "s3" or "r2" => 3,
            _ => 50,
        };
    }

    /// <summary>
    /// Quick filesystem check — the encoder drops outputs under a sibling
    /// directory named after the media title. If that directory exists with
    /// any files in it, consider the file already encoded.
    /// </summary>
    private bool IsAlreadyEncoded(VideoFile file)
    {
        if (string.IsNullOrEmpty(file.HostFolder))
            return false;

        // The encoder writes into `{HostFolder}` itself for single-file
        // outputs (mkv/mp4/m4a) or into a `{HostFolder}/*.NoMercy/` subdir
        // for HLS. Either way, any *.m3u8 / *.mp4 / *.mkv / *.m4a sibling
        // of the source is a reasonable indicator the file is encoded.
        string sourceExt = Path.GetExtension(file.Filename).ToLowerInvariant();
        try
        {
            if (!storage.Exists(file.HostFolder))
                return false;

            // Any .NoMercy subdirectory counts as encoded.
            if (
                storage.List(file.HostFolder, "*.NoMercy", recursive: false).Any(e => e.IsDirectory)
            )
                return true;

            // Any master playlist alongside counts too.
            if (storage.List(file.HostFolder, "*.m3u8", recursive: false).Any(e => !e.IsDirectory))
                return true;

            _ = sourceExt;
            return false;
        }
        catch
        {
            return false;
        }
    }
}
