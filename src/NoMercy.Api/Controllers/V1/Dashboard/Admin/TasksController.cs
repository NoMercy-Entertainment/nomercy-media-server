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

using System.Collections.Immutable;
using System.Diagnostics;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using NoMercy.Api.Controllers.V1.Music;
using NoMercy.Api.DTOs.Common;
using NoMercy.Api.DTOs.Dashboard;
using NoMercy.Api.DTOs.Music;
using NoMercy.Api.Services;
using NoMercy.Data.Repositories;
using NoMercy.Database;
using NoMercy.Database.Models.Encoder;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Media;
using NoMercy.Database.Models.Movies;
using NoMercy.Database.Models.Music;
using NoMercy.Database.Models.Queue;
using NoMercy.Database.Models.TvShows;
using NoMercy.Encoder.Execution;
using NoMercy.Encoder.Profiles;
using NoMercy.Events;
using NoMercy.Events.Encoding;
using NoMercy.MediaProcessing.Jobs.MediaJobs;
using NoMercy.MediaProcessing.Jobs.MediaJobs.Support;
using NoMercy.MediaProcessing.Jobs.SubtitleJobs;
using NoMercy.NmSystem.Domain;
using NoMercy.NmSystem.Extensions;
using NoMercy.NmSystem.NewtonSoftConverters;
using NoMercy.NmSystem.SystemCalls;
using NoMercy.Queue.MediaServer;
using NoMercyQueue;
using NoMercyQueue.Core;
using MediaJobDispatcher = NoMercy.MediaProcessing.Jobs.JobDispatcher;

namespace NoMercy.Api.Controllers.V1.Dashboard.Admin;

[ApiController]
[Tags("Dashboard Tasks")]
[ApiVersion(1.0)]
[Authorize(Policy = "Moderator")]
[Route("api/v{version:apiVersion}/dashboard/tasks", Order = 10)]
public class TasksController(
    MediaContext mediaContext,
    IDbContextFactory<MediaContext> mediaContextFactory,
    IDbContextFactory<QueueContext> queueContextFactory,
    IEncoderProcessRegistry processRegistry,
    ProcessThrottle processThrottle,
    IEncodingHistoryRepository historyRepository
) : BaseController
{
    [HttpGet]
    public async Task<IActionResult> Index()
    {
        await using QueueContext queueContext = await queueContextFactory.CreateDbContextAsync();

        // Cap the load: the queue table retains history and grows unbounded, so
        // materializing every row here was seconds of work for a monitor view that
        // only shows the highest-priority pending tasks.
        List<QueueJob> jobs = await queueContext
            .QueueJobs.OrderByDescending(j => j.Priority)
            .ThenBy(j => j.Id)
            .Take(UiLimits.MaximumTasksInList)
            .ToListAsync();

        List<TaskDto> list =
        [
            .. jobs.Select(job => new TaskDto
            {
                Id = job.Id.ToString(),
                Title = ResolveJobTitle(job),
                Value = 0,
                Type = job.Queue,
                CreatedAt = job.CreatedAt,
                UpdatedAt = job.ReservedAt ?? job.CreatedAt,
            }),
        ];

        return Ok(list);
    }

    /// <summary>
    /// The presets an encode will run with: every preset linked to its folder,
    /// narrowed to one only when the job named it.
    ///
    /// <para>This is <see cref="VideoEncodeJob.HandleInitialRunAsync"/>'s own
    /// rule, and it has to stay that way or the card describes work the encoder
    /// is not doing. A folder carrying "4K HDR HEVC" and "1080p SDR HEVC"
    /// produces both in one coordinated run, so naming either alone told half
    /// the story. <c>IsDefault</c> selects nothing here — the encoder never
    /// reads it. A named preset the folder does not link runs nothing at all,
    /// which is an empty list rather than a fallback.</para>
    /// </summary>
    private static Ulid[] PresetsFor(VideoEncodeJob job, Dictionary<Ulid, Folder> folderById)
    {
        Ulid[] linked =
            folderById
                .GetValueOrDefault(job.FolderId)
                ?.EncodingPresetFolders.Select(link => link.PresetId)
                .ToArray()
            ?? [];

        if (job.PresetId is not Ulid named)
            return linked;

        return linked.Contains(named) ? [named] : [];
    }

    /// <summary>
    /// What the encode will produce, resolved once per set of presets per
    /// request — a season's worth of episodes shares one folder's presets, and
    /// resolving each walks the inheritance chain and parses JSON.
    ///
    /// <para>Several presets are one merged answer, because they are one
    /// coordinated encode: the runner shares the analysis, the audio and the
    /// subtitles across them and writes a single master playlist listing every
    /// preset's renditions.</para>
    ///
    /// <para>A preset that has been deleted, or whose chain no longer resolves,
    /// leaves the plan off the card rather than failing the whole panel: the
    /// queue is the answer this endpoint owes, and the plan is an extra on top
    /// of it. The cache holds the null too, so a broken preset is attempted
    /// once.</para>
    /// </summary>
    private static QueueJobPlanDto? ResolvePlan(
        Ulid[] presetIds,
        IPresetLookup lookup,
        Dictionary<string, QueueJobPlanDto?> cache
    )
    {
        if (presetIds.Length == 0)
            return null;

        string key = string.Join('|', presetIds);
        if (cache.TryGetValue(key, out QueueJobPlanDto? cached))
            return cached;

        QueueJobPlanDto? plan;
        try
        {
            plan = QueueJobPlanMapper.Merge([
                .. presetIds.Select(id =>
                    QueueJobPlanMapper.FromProfile(PresetResolver.Resolve(id, lookup))
                ),
            ]);
        }
        catch (Exception exception)
        {
            Logger.Encoder($"Queue card plan unavailable for {key}: {exception.Message}");
            plan = null;
        }

        cache[key] = plan;
        return plan;
    }

    /// <summary>
    /// Best-effort human-readable job type for the dashboard task list. Falls
    /// back to the raw queue name if the payload can't be deserialized to its
    /// concrete job type (e.g. a stale payload from a removed job class).
    /// </summary>
    private static string ResolveJobTitle(QueueJob job)
    {
        try
        {
            return SerializationHelper.Deserialize<object>(job.Payload).GetType().Name;
        }
        catch (Exception)
        {
            return job.Queue;
        }
    }

    /// <summary>
    /// There is no create/update/delete verb for the task collection itself —
    /// a task is a queue row, and the real surface for that is
    /// <c>queue/{id}</c> (update priority, delete) plus the dispatch jobs
    /// that create rows in the first place. This route never had an
    /// implementation to fall back to, so it answers honestly instead of a
    /// 200 that did nothing.
    /// </summary>
    [HttpPost]
    public IActionResult Store()
    {
        return NotImplementedResponse(
            "Creating a task directly is not supported. Dispatch a job instead."
        );
    }

    [HttpPatch]
    public IActionResult Update()
    {
        return NotImplementedResponse(
            "Updating the task collection is not supported. Use PATCH queue/{id} for a single job."
        );
    }

    [HttpDelete]
    public IActionResult Destroy()
    {
        return NotImplementedResponse(
            "Clearing the task collection is not supported. Use DELETE queue/{id} for a single job."
        );
    }

    [HttpPost]
    [Route("pause/{id:int}")]
    public IActionResult PauseTask(int id)
    {
        IReadOnlyCollection<int> pids = processRegistry.GetProcessIds(id);
        if (pids.Count == 0)
            return Ok(
                new StatusResponseDto<string>
                {
                    Status = "not_found",
                    Message = "No running process for that task",
                }
            );

        foreach (int pid in pids)
            processThrottle.Suspend(pid);

        return Ok(new StatusResponseDto<string> { Status = "success", Message = "Task paused" });
    }

    [HttpPost]
    [Route("resume/{id:int}")]
    public IActionResult ResumeTask(int id)
    {
        IReadOnlyCollection<int> pids = processRegistry.GetProcessIds(id);
        if (pids.Count == 0)
            return Ok(
                new StatusResponseDto<string>
                {
                    Status = "not_found",
                    Message = "No running process for that task",
                }
            );

        foreach (int pid in pids)
            processThrottle.Resume(pid);

        return Ok(new StatusResponseDto<string> { Status = "success", Message = "Task resumed" });
    }

    /// <summary>
    /// Active task-worker threads across every queue, straight from
    /// <see cref="QueueRunner.GetActiveWorkerThreads"/> — the count the
    /// dashboard's pause toggle derives its state from (workers &gt; 0 means
    /// running).
    /// </summary>
    [HttpGet]
    [Route("runners")]
    public IActionResult RunningTaskWorkers()
    {
        int workers = QueueRunner.Current?.GetActiveWorkerThreads().Count ?? 0;

        return Ok(new RunnersResponseDto { Status = "ok", Workers = workers });
    }

    [HttpGet]
    [Route("queue")]
    public async Task<IActionResult> EncoderQueue()
    {
        await using QueueContext queueContext = await queueContextFactory.CreateDbContextAsync();

        // This panel polls, and the encoder queue is thousands of rows deep on a
        // library mid-encode. Tracking every one of them per poll is pure cost:
        // nothing here is written back.
        //
        // Bounded, for two failures rather than one.
        //
        // Unbounded, the sort had to buffer every matching row to order it,
        // payload included, so a full encoder queue asked SQLite to spill
        // gigabytes of temp and the poll died on "database or disk is full".
        //
        // Bounded by a single top-N, the whole panel came out of the busiest
        // priority band: an album import queues music at 5 while video encodes
        // sit at 4 and 0, so 13,779 music rows took every slot and the operator's
        // video encodes were missing from a panel that claimed to list the queue.
        // A share per band costs one small indexed query each and cannot starve.
        // MusicEncodeJob runs on encoder-cpu, not the plain 'encoder' queue video
        // uses, so every query below that used to read one queue now reads both —
        // scoped to MusicEncodeJob payloads only, so nothing else on encoder-cpu
        // leaks into this panel.
        List<int> priorities = await queueContext
            .QueueJobs.AsNoTracking()
            .Where(j =>
                j.Queue == "encoder"
                || (j.Queue == "encoder-cpu" && j.Payload.Contains("MusicEncodeJob"))
            )
            .Select(j => j.Priority)
            .Distinct()
            .OrderByDescending(priority => priority)
            .ToListAsync();

        int perBand = Math.Max(1, UiLimits.MaximumTasksInList / Math.Max(1, priorities.Count));

        List<QueueJob> banded = [];
        foreach (int priority in priorities)
        {
            banded.AddRange(
                await queueContext
                    .QueueJobs.AsNoTracking()
                    .Where(j =>
                        (
                            j.Queue == "encoder"
                            || (j.Queue == "encoder-cpu" && j.Payload.Contains("MusicEncodeJob"))
                        )
                        && j.Priority == priority
                    )
                    .OrderBy(j => j.Id)
                    .Take(perBand)
                    .ToListAsync()
            );
        }

        // Work in flight is never a candidate for omission. It is a handful of
        // rows, and a running encode missing from the panel is the one thing the
        // operator is most certainly looking for.
        List<QueueJob> running = await queueContext
            .QueueJobs.AsNoTracking()
            .Where(j =>
                (
                    j.Queue == "encoder"
                    || (j.Queue == "encoder-cpu" && j.Payload.Contains("MusicEncodeJob"))
                )
                && j.ReservedAt != null
            )
            .OrderBy(j => j.Id)
            .Take(UiLimits.MaximumTasksInList)
            .ToListAsync();

        // A video encode is one job/one row end to end now — the SAME row that
        // decomposed also runs every bundle's ffmpeg inline, so a runner
        // actually working an encode holds this row's own reservation for as
        // long as it runs. The "running" query above already catches every
        // in-flight encode; there is no separate child row to cross-reference.
        ImmutableList<QueueJob> jobs =
        [
            .. banded.Concat(running).GroupBy(row => row.Id).Select(group => group.First()),
        ];

        // Each parsed payload stays paired with the row it came from: a payload
        // that fails to deserialize is dropped here, so indexing the unfiltered
        // row list by the parsed list's position would hand every later job the
        // preceding row's id, priority and reservation.
        //
        // Parsed once and then split. Deserializing again to find the rows that
        // are not encodes cost a second pass over every payload on the queue,
        // and this endpoint is polled.
        List<QueueJobEntry> parsedJobs =
        [
            .. jobs.Select(row => new QueueJobEntry(row, ReadEncodeJob(row.Payload))),
        ];

        List<QueueJobEntry> encoderJobs = [.. parsedJobs.Where(entry => entry.Job is not null)];

        // A music encode is an encode, but it is not a VideoEncodeJob, so it read
        // as neither one thing nor the other and was dropped from both lists.
        // Video rows further down the queue hid that until an album import put
        // thousands of music encodes above every one of them and the panel went
        // blank while the server encoded an album.
        List<MusicQueueRow> musicRows =
        [
            .. parsedJobs
                .Where(entry => entry.Job is null)
                .Select(entry => ReadMusicEncodeJob(entry.Row))
                .Where(entry => entry is not null)
                .Select(entry => entry!),
        ];

        List<QueueJobDto> musicJobs = await BuildAlbumCards(musicRows, queueContext);

        HashSet<int> musicRowIds = [.. musicRows.Select(row => row.Row.Id)];

        // Maintenance work sharing the encoder queue — preview rebuilds and
        // subtitle OCR — reported as itself rather than dressed up as an encode.
        // Hours of this can be queued at once, and a panel that shows none of it
        // is telling the operator the server is idle while it works.
        List<MaintenanceJob> maintenanceJobs =
        [
            .. parsedJobs
                .Where(entry => entry.Job is null && !musicRowIds.Contains(entry.Row.Id))
                .Select(entry => ReadMaintenanceJob(entry.Row))
                .Where(entry => entry is not null)
                .Select(entry => entry!),
        ];

        await EnrichMaintenanceJobsAsync(maintenanceJobs);

        // Parse each job id once — Id is either an int (movie/episode) or a Guid (track).
        List<int> movieOrEpisodeIds = [];
        List<Guid> trackIds = [];

        foreach (VideoEncodeJob encoderJob in encoderJobs.Select(entry => entry.Job!))
        {
            int intId = encoderJob.Id.ToInt();
            if (intId != 0)
            {
                movieOrEpisodeIds.Add(intId);
            }
            else
            {
                Guid guidId = encoderJob.Id.ToGuid();
                if (guidId != Guid.Empty)
                    trackIds.Add(guidId);
            }
        }

        List<Ulid> folderIds = [.. encoderJobs.Select(entry => entry.Job!.FolderId).Distinct()];

        // Folders — only the profile include needed for the Profile field; no library graph.
        List<Folder> folders = await mediaContext
            .Folders.AsNoTracking()
            .Where(f => folderIds.Contains(f.Id))
            .Include(f => f.EncodingPresetFolders)
                .ThenInclude(link => link.Preset)
            .ToListAsync();

        Dictionary<Ulid, Folder> folderById = folders.ToDictionary(f => f.Id);

        // Load only the entities actually referenced by queued jobs.
        Dictionary<int, Movie> movieById = [];
        Dictionary<int, Episode> episodeById = [];
        Dictionary<Guid, Track> trackById = [];

        if (movieOrEpisodeIds.Count > 0)
        {
            List<Movie> movies = await mediaContext
                .Movies.AsNoTracking()
                .Where(m => movieOrEpisodeIds.Contains(m.Id))
                .ToListAsync();

            foreach (Movie movie in movies)
                movieById[movie.Id] = movie;

            // Episodes need Tv for CreateTitle (Tv.Title, SeasonNumber, EpisodeNumber).
            List<Episode> episodes = await mediaContext
                .Episodes.AsNoTracking()
                .Where(e => movieOrEpisodeIds.Contains(e.Id))
                .Include(e => e.Tv)
                .ToListAsync();

            foreach (Episode episode in episodes)
                episodeById[episode.Id] = episode;
        }

        if (trackIds.Count > 0)
        {
            // Tracks need AlbumTrack → Album for CreateName.
            List<Track> tracks = await mediaContext
                .Tracks.AsNoTracking()
                .Where(t => trackIds.Contains(t.Id))
                .Include(t => t.AlbumTrack)
                    .ThenInclude(at => at.Album)
                .ToListAsync();

            foreach (Track track in tracks)
                trackById[track.Id] = track;
        }

        Dictionary<Ulid, string> presetNameById = folders
            .SelectMany(folder => folder.EncodingPresetFolders)
            .Where(link => link.Preset is not null)
            .GroupBy(link => link.PresetId)
            .ToDictionary(group => group.Key, group => group.First().Preset!.Name);

        DbPresetLookup presetLookup = new(mediaContext);
        Dictionary<string, QueueJobPlanDto?> planByPresetSet = [];

        QueueJobDto[] queueJobs =
        [
            .. encoderJobs
                .Select(entry =>
                {
                    VideoEncodeJob j = entry.Job!;
                    Ulid[] presetIds = PresetsFor(j, folderById);

                    return new QueueJobDto
                    {
                        Id = entry.Row.Id,
                        Priority = entry.Row.Priority,
                        PayloadId = j.Id,
                        Title = ResolveTitle(j, movieById, episodeById, trackById),
                        Backdrop = ResolveArtwork(j, movieById, episodeById),
                        Type = j.GetType().Name,
                        Status = IsEncodeInFlight(entry.Row.ReservedAt) ? "running" : "pending",
                        InputFile = j.InputFile,
                        Profile =
                            presetIds.Length == 0
                                ? null
                                : string.Join(
                                    ", ",
                                    presetIds.Select(id =>
                                        presetNameById.GetValueOrDefault(id, id.ToString())
                                    )
                                ),
                        Plan = ResolvePlan(presetIds, presetLookup, planByPresetSet),
                    };
                })
                .Concat(musicJobs)
                .Concat(maintenanceJobs.Select(entry => entry.Dto))
                // Ordered here, not in SQL: the row is rewritten in place on every
                // coordinator wake-up, so SQL can't stably sort on it mid-query.
                // Priority leads, because that is what the runner honours.
                .OrderByDescending(dto => dto.Priority)
                // Only a row ACTUALLY holding a reservation right now is really
                // ahead of everything still waiting — a row that has merely
                // decomposed is not "is running", so that can never be the
                // ranking key.
                .ThenByDescending(dto => dto.Status == "running")
                // The row's own id is the true original dispatch position and
                // never changes across the encode's whole lifetime — one job,
                // one row, from decompose through the last bundle.
                .ThenBy(dto => dto.Id),
        ];

        // Catch-up broadcast so a reloaded dashboard gets a card back for work
        // already in flight. Keyed off the reservation rather than the reported
        // status: a coordinator sleeping between poll wake-ups reports running
        // but has no live encoder behind it, and a card for it would only flap
        // in and out as the idle sweep collected it.
        HashSet<int> reservedRowIds =
        [
            .. encoderJobs
                .Where(entry => entry.Row.ReservedAt is not null)
                .Select(entry => entry.Row.Id),
        ];

        if (EventBusProvider.IsConfigured)
        {
            foreach (QueueJobDto dto in queueJobs.Where(dto => reservedRowIds.Contains(dto.Id)))
            {
                // Lower-case keys are the encoder-progress contract — every other
                // producer on this channel emits them, and the serializer has no
                // naming strategy, so an anonymous object with PascalCase members
                // reached the dashboard as {Id, Status, Title}. Reading data.status
                // off that gives undefined, which is not in-flight, so the card was
                // never restored and the miss ran the handler's removal branch
                // instead — one refetch per running row per poll, each refetch
                // re-broadcasting the same unreadable payload.
                _ = EventBusProvider.Current.PublishAsync(
                    new EncodingProgressBroadcastedEvent
                    {
                        ProgressData = new
                        {
                            id = dto.PayloadId.ToInt(),
                            status = "running",
                            title = dto.Title,
                            message = "Encoding video",
                        },
                    }
                );
            }
        }

        return Ok(new DataResponseDto<QueueJobDto[]> { Data = queueJobs });
    }

    /// <summary>
    /// The encode behind a queue row, or null when that row is not an encode.
    ///
    /// <para>The <c>encoder</c> queue is shared: subtitle OCR backfills and preview
    /// rebuilds run there too, deliberately, so maintenance work never competes with
    /// an encode for the same hardware. Deserializing straight into
    /// <see cref="VideoEncodeJob"/> did not reject those — they carry a
    /// <c>FolderId</c> of their own, so the result was a card with a real profile
    /// and nothing else: no title, no id, no artwork, no file. Payloads record
    /// their own type, so ask for it rather than assume it.</para>
    /// </summary>
    internal static VideoEncodeJob? ReadEncodeJob(string payload)
    {
        try
        {
            return SerializationHelper.Deserialize<object>(payload) as VideoEncodeJob;
        }
        catch (Exception)
        {
            // A payload written by a build that has since renamed or removed the
            // job type. Not this endpoint's problem to report.
            return null;
        }
    }

    /// <summary>
    /// A queue row read as a music encode, or null when the row is something else.
    /// </summary>
    internal static MusicQueueRow? ReadMusicEncodeJob(QueueJob row)
    {
        MusicEncodeJob? job;
        try
        {
            job = SerializationHelper.Deserialize<object>(row.Payload) as MusicEncodeJob;
        }
        catch (Exception)
        {
            return null;
        }

        return job is null ? null : new(row, job);
    }

    /// <summary>
    /// A queued album as one card that fills up, rather than one card per track.
    ///
    /// <para>An album is a single piece of work to the person who asked for it. As
    /// a card per track a twenty-track release pushed everything else off the
    /// panel and each card sat at nothing until its own second of encoding, so
    /// the panel showed no movement while an album was clearly being worked
    /// through. One card counting its tracks says the same thing in one row and
    /// actually moves.</para>
    ///
    /// <para>Deliberately carries no <see cref="QueueJobDto.Kind"/>: kind is what
    /// marks a row as maintenance, and the dashboard renders any kind it does not
    /// recognise as generic maintenance — no artwork, no progress. This is an
    /// encode and reports as one.</para>
    /// </summary>
    private async Task<List<QueueJobDto>> BuildAlbumCards(
        List<MusicQueueRow> musicRows,
        QueueContext queueContext
    )
    {
        if (musicRows.Count == 0)
            return [];

        // Counted over the WHOLE queue, not over the sampled rows this listing
        // holds: the listing reads a share of each priority band, so the tracks it
        // happens to carry say nothing about how much of the album is left. A card
        // reporting "3 of 20" from a sample would count down as the panel scrolled.
        Dictionary<Guid, int> remainingByRelease = await CountQueuedTracksByRelease(queueContext);

        List<Guid> releaseIds = [.. musicRows.Select(row => row.Job.ReleaseId).Distinct()];

        // Encoded-so-far is the AlbumTrack link count, not Album.Tracks: the
        // metadata-declared total is set once at import from what the provider
        // says the release holds, and never moves — a card built on it could sit
        // at "44 of 44" while genuinely nothing had been encoded, or worse, look
        // complete while tracks were still queued, because the ceiling itself
        // was wrong. AlbumTrack is written per track as its recording is stored,
        // so its count is the real numerator, and total is that plus whatever is
        // still queued for the release — the pair still reaches 100% exactly
        // when the queue for it runs dry.
        Dictionary<Guid, int> encodedTracksByRelease = await mediaContext
            .AlbumTrack.AsNoTracking()
            .Where(link => releaseIds.Contains(link.AlbumId))
            .GroupBy(link => link.AlbumId)
            .ToDictionaryAsync(group => group.Key, group => group.Count());

        Dictionary<Guid, string?> covers = await mediaContext
            .Albums.AsNoTracking()
            .Where(album => releaseIds.Contains(album.Id))
            .Select(album => new AlbumCover(album.Id, album.Cover))
            .ToDictionaryAsync(album => album.Id, album => album.Cover);

        // The same release cover, read off the album's tracks when the album row
        // itself has none. It is one image per release and every track stores it,
        // but the album row is written before the cover-art job answers and only
        // that job fills it in — so 106 of the 885 queued albums have a blank
        // column while their own tracks carry the artwork. Still the release's
        // cover: an artist's picture would be a different album's image.
        List<Guid> uncovered = [.. releaseIds.Where(id => covers.GetValueOrDefault(id) is null)];

        if (uncovered.Count > 0)
        {
            List<AlbumCover> trackCovers = await mediaContext
                .AlbumTrack.AsNoTracking()
                .Where(link => uncovered.Contains(link.AlbumId) && link.Track.Cover != null)
                .Select(link => new AlbumCover(link.AlbumId, link.Track.Cover))
                .ToListAsync();

            foreach (IGrouping<Guid, AlbumCover> group in trackCovers.GroupBy(row => row.Id))
                covers[group.Key] = group.First().Cover;
        }

        return
        [
            .. musicRows
                .GroupBy(row => row.Job.ReleaseId)
                .Select(group =>
                {
                    MusicQueueRow first = group.OrderBy(row => row.Row.Id).First();
                    MusicQueueRow current =
                        group.FirstOrDefault(row => row.Row.ReservedAt is not null) ?? first;

                    // Completed is what AlbumTrack already links (real encoded
                    // work); total adds back what is still queued for the release,
                    // so the pair reaches 100% exactly when the queue runs dry.
                    int remaining = remainingByRelease.GetValueOrDefault(group.Key, group.Count());
                    int completed = encodedTracksByRelease.GetValueOrDefault(group.Key, 0);
                    int total = completed + remaining;

                    string title = string.Join(
                        " - ",
                        new[] { first.Job.ArtistName, first.Job.ReleaseName }.Where(part =>
                            !string.IsNullOrWhiteSpace(part)
                        )
                    );

                    return new QueueJobDto
                    {
                        // The card acts on a real row — the one being encoded, or the
                        // next in line — so removing it removes work that exists.
                        Id = current.Row.Id,
                        Priority = current.Row.Priority,
                        // The release, so an album is one card. Progress events key on
                        // the track, so a live overlay finds nothing here and the
                        // track count is what moves — which is the point.
                        PayloadId = group.Key.ToString(),
                        Title = title.Length > 0 ? title : Path.GetFileName(first.Job.InputFile),
                        // Rooted here, because a release's cover is not served from
                        // where a backdrop is. Every client roots this field at
                        // /images/original, so an album with perfectly good artwork
                        // drew an empty box.
                        Backdrop = covers.GetValueOrDefault(group.Key) is { } cover
                            ? $"/images/music{cover}"
                            : null,
                        Type = nameof(MusicEncodeJob),
                        Status = group.Any(row => row.Row.ReservedAt is not null)
                            ? "running"
                            : "pending",
                        InputFile = current.Job.InputFile,
                        CompletedItems = completed,
                        TotalItems = total,
                        Progress = total > 0 ? Math.Round(completed * 100d / total, 1) : null,
                    };
                }),
        ];
    }

    /// <summary>
    /// Tracks still queued per release, counted in the database rather than by
    /// materializing thousands of payloads into the poll that asks.
    /// </summary>
    private static async Task<Dictionary<Guid, int>> CountQueuedTracksByRelease(
        QueueContext queueContext
    )
    {
        List<ReleaseTrackCount> counts = await queueContext
            .Database.SqlQueryRaw<ReleaseTrackCount>(
                """
                SELECT json_extract(Payload, '$.releaseId') AS ReleaseId,
                       COUNT(*) AS Remaining
                FROM QueueJobs
                WHERE Queue = 'encoder-cpu'
                  AND Payload LIKE '%MusicEncodeJob%'
                GROUP BY json_extract(Payload, '$.releaseId')
                """
            )
            .ToListAsync();

        Dictionary<Guid, int> byRelease = [];
        foreach (ReleaseTrackCount count in counts)
            if (Guid.TryParse(count.ReleaseId, out Guid releaseId))
                byRelease[releaseId] = count.Remaining;

        return byRelease;
    }

    /// <summary>
    /// A queue row that is maintenance rather than an encode, described as
    /// itself. Returns null for anything this endpoint has no name for, so an
    /// unrecognised job stays out of the panel instead of appearing as a blank.
    ///
    /// <para>The title here is the one the job carries — a folder name. It is a
    /// fallback: <see cref="EncoderQueue"/> replaces it with the library's own
    /// title, and adds the still, for every folder it can resolve.</para>
    /// </summary>
    internal static MaintenanceJob? ReadMaintenanceJob(QueueJob row)
    {
        object? job;
        try
        {
            job = SerializationHelper.Deserialize<object>(row.Payload);
        }
        catch (Exception)
        {
            return null;
        }

        (string kind, string title, string target, string hostFolder) = job switch
        {
            SpriteSheetUpgradeJob sprite => (
                "preview",
                sprite.Title,
                sprite.HostFolder,
                sprite.HostFolder
            ),
            SubtitleOcrBackfillJob ocr => (
                "subtitles",
                $"{ocr.MediaTitle} ({ocr.Language})",
                ocr.SupFileName,
                ocr.HostFolder
            ),
            _ => (string.Empty, string.Empty, string.Empty, string.Empty),
        };

        if (kind.Length == 0)
            return null;

        QueueJobDto dto = new()
        {
            Id = row.Id,
            Priority = row.Priority,
            // What the work is about and what does not move, which is what a list
            // key needs. Two OCR passes over one folder differ by their sup file,
            // so that is the key there; a preview rebuild is one per folder.
            PayloadId = target,
            Title = title,
            Type = job!.GetType().Name,
            Kind = kind,
            Status = row.ReservedAt is not null ? "running" : "pending",
            InputFile = target,
        };

        return new(dto, hostFolder);
    }

    /// <summary>
    /// Gives every maintenance row the title and the still of the file it is
    /// about. The job payload only carries a folder name, and a card built from
    /// that alone is a grey frame over a filename — for work that can occupy the
    /// server for hours. The folder is already the unique key of a
    /// <see cref="VideoFile"/>, so the picture and the proper title are one
    /// query away.
    /// </summary>
    private async Task EnrichMaintenanceJobsAsync(List<MaintenanceJob> maintenanceJobs)
    {
        List<string> hostFolders =
        [
            .. maintenanceJobs
                .Select(entry => entry.HostFolder)
                .Where(folder => folder.Length > 0)
                .Distinct(),
        ];

        if (hostFolders.Count == 0)
            return;

        List<VideoFile> files = await mediaContext
            .VideoFiles.AsNoTracking()
            .Where(file => hostFolders.Contains(file.HostFolder))
            .Include(file => file.Episode)
                .ThenInclude(episode => episode!.Tv)
            .Include(file => file.Movie)
            .ToListAsync();

        Dictionary<string, VideoFile> fileByFolder = [];
        foreach (VideoFile file in files)
            fileByFolder.TryAdd(file.HostFolder, file);

        foreach (MaintenanceJob entry in maintenanceJobs)
        {
            if (fileByFolder.TryGetValue(entry.HostFolder, out VideoFile? file))
                ApplyMediaDetails(entry.Dto, file);
        }
    }

    /// <summary>
    /// Puts the library's own title and still onto a maintenance card. A file with
    /// neither an episode nor a movie behind it keeps the folder name the job
    /// carried: a card naming the folder still beats a card naming nothing.
    /// </summary>
    internal static void ApplyMediaDetails(QueueJobDto dto, VideoFile file)
    {
        if (file.Episode is { Tv: not null })
        {
            dto.Title = file.Episode.CreateTitle();
            dto.Backdrop = file.Episode.Still;
            return;
        }

        if (file.Movie is not null)
        {
            dto.Title = file.Movie.CreateTitle();
            dto.Backdrop = file.Movie.Backdrop;
        }
    }

    /// <summary>
    /// Whether an encode is actually being worked on, which is the line the
    /// dashboard splits "encoding now" from "waiting" on.
    ///
    /// <para>Carrying coordinator state is not that line — a coordinator
    /// stamps itself the moment it has decomposed, and with one encoder
    /// runner a dozen of those can sit behind each other having done
    /// nothing. One job runs every bundle inline on its own row now, so a
    /// runner actually working an encode holds THIS row's reservation for as
    /// long as it runs — there is no separate child row to check.</para>
    /// </summary>
    internal static bool IsEncodeInFlight(DateTime? reservedAt) => reservedAt is not null;

    /// <summary>
    /// The artwork for a queued encode — a movie's backdrop or an episode's still,
    /// the same image the encoder's progress events carry once the job is running,
    /// so the card does not change picture when it starts.
    /// </summary>
    private static string? ResolveArtwork(
        VideoEncodeJob j,
        Dictionary<int, Movie> movieById,
        Dictionary<int, Episode> episodeById
    )
    {
        int intId = j.Id.ToInt();
        if (intId == 0)
            return null;

        if (movieById.TryGetValue(intId, out Movie? movie))
            return movie.Backdrop;

        return episodeById.TryGetValue(intId, out Episode? episode) ? episode.Still : null;
    }

    private static string ResolveTitle(
        VideoEncodeJob j,
        Dictionary<int, Movie> movieById,
        Dictionary<int, Episode> episodeById,
        Dictionary<Guid, Track> trackById
    )
    {
        int intId = j.Id.ToInt();
        if (intId != 0)
        {
            if (movieById.TryGetValue(intId, out Movie? movie))
                return movie.CreateTitle();

            if (episodeById.TryGetValue(intId, out Episode? episode))
                return episode.CreateTitle();
        }

        Guid guidId = j.Id.ToGuid();
        if (guidId != Guid.Empty && trackById.TryGetValue(guidId, out Track? track))
            return track.CreateName();

        return string.Empty;
    }

    [HttpDelete]
    [Route("queue/{id:int}")]
    public async Task<IActionResult> DeleteTask(int id)
    {
        await using QueueContext queueContext = await queueContextFactory.CreateDbContextAsync();
        QueueJob? job = queueContext.QueueJobs.FirstOrDefault(job => job.Id == id);

        if (job is null)
            return NotFoundResponse("Job not found");

        // If the job is currently running, terminate the FFmpeg process(es) tracked
        // for it. Without this the ffmpeg process keeps going after the queue entry
        // is removed — V1 dashboard behavior expects the kill.
        VideoEncodeJob? payload = job.Payload.FromJson<VideoEncodeJob>();
        if (payload is not null && int.TryParse(payload.Id, out int mediaId))
        {
            IReadOnlyCollection<int> pids = processRegistry.GetProcessIds(mediaId);
            foreach (int pid in pids)
            {
                try
                {
                    using Process ffmpegProcess = Process.GetProcessById(pid);
                    ffmpegProcess.Kill(entireProcessTree: true);
                }
                catch (Exception)
                {
                    // Process may have already exited or the PID may be stale —
                    // clean up the registry entry regardless.
                }
                processRegistry.Unregister(mediaId, pid);
            }
        }

        queueContext.QueueJobs.Remove(job);

        await queueContext.SaveChangesAsync();

        return Ok(new StatusResponseDto<string> { Message = "Job removed", Status = "success" });
    }

    [HttpPatch]
    [Route("queue/{id:int}")]
    public async Task<IActionResult> UpdateTask(int id, [FromBody] PatchQueueItemDto request)
    {
        await using QueueContext queueContext = await queueContextFactory.CreateDbContextAsync();

        QueueJob? job = queueContext.QueueJobs.FirstOrDefault(job => job.Id == id);

        if (job is null)
            return NotFoundResponse("Job not found");

        job.Priority = request.Priority;

        await queueContext.SaveChangesAsync();

        return Ok(
            new StatusResponseDto<string> { Message = "Priority updated", Status = "success" }
        );
    }

    /// <summary>
    /// Stop dispatching new encoder jobs. In-flight FFmpeg processes keep running —
    /// use the per-task pause endpoint to suspend those separately.
    /// </summary>
    [HttpPost]
    [Route("pause-queue")]
    public async Task<IActionResult> PauseEncoderQueue()
    {
        if (QueueRunner.Current is null)
            return Ok(
                new StatusResponseDto<string>
                {
                    Message = "Queue runner not available",
                    Status = "unavailable",
                }
            );

        foreach (string queue in EncoderQueueFamily)
            await QueueRunner.Current.Pause(queue);

        SuspendRunningEncodes();
        return Ok(
            new StatusResponseDto<string> { Message = "Encoder queue paused", Status = "success" }
        );
    }

    /// <summary>Resume dispatching jobs from the encoder queue.</summary>
    [HttpPost]
    [Route("resume-queue")]
    public async Task<IActionResult> ResumeEncoderQueue()
    {
        if (QueueRunner.Current is null)
            return Ok(
                new StatusResponseDto<string>
                {
                    Message = "Queue runner not available",
                    Status = "unavailable",
                }
            );

        ResumeRunningEncodes();

        foreach (string queue in EncoderQueueFamily)
            await QueueRunner.Current.Resume(queue);
        return Ok(
            new StatusResponseDto<string> { Message = "Encoder queue resumed", Status = "success" }
        );
    }

    /// <summary>
    /// Stop dispatching audio-analysis work. Analysis is opt-in background work
    /// that costs real CPU per track, so an operator needs to stop it without
    /// stopping an encode — and without waiting for a library to finish.
    /// <para>
    /// It shares the music queue with lyrics and metadata jobs, so this pauses
    /// those too. That is the honest scope of the control and the dashboard
    /// should say so.
    /// </para>
    /// </summary>
    [HttpPost]
    [Route("pause-music-queue")]
    public async Task<IActionResult> PauseMusicQueue()
    {
        if (QueueRunner.Current is null)
            return Ok(
                new StatusResponseDto<string>
                {
                    Message = "Queue runner not available",
                    Status = "unavailable",
                }
            );

        await QueueRunner.Current.Pause(QueueNames.Music);

        return Ok(
            new StatusResponseDto<string> { Message = "Music queue paused", Status = "success" }
        );
    }

    [HttpPost]
    [Route("resume-music-queue")]
    public async Task<IActionResult> ResumeMusicQueue()
    {
        if (QueueRunner.Current is null)
            return Ok(
                new StatusResponseDto<string>
                {
                    Message = "Queue runner not available",
                    Status = "unavailable",
                }
            );

        await QueueRunner.Current.Resume(QueueNames.Music);

        return Ok(
            new StatusResponseDto<string> { Message = "Music queue resumed", Status = "success" }
        );
    }

    /// <summary>
    /// Paused state and remaining depth for audio analysis, so the dashboard can
    /// show progress rather than only a spinner. Depth is a count: the queue
    /// listing returns a bounded sample, so counting the cards it hands back
    /// under-reports a large library by orders of magnitude.
    /// </summary>
    [HttpGet]
    [Route("audio-analysis/status")]
    public async Task<IActionResult> AudioAnalysisStatus()
    {
        bool paused = IsQueuePausedOrFalse(QueueNames.Music);

        await using QueueContext queueContext = await queueContextFactory.CreateDbContextAsync();

        int queued = await queueContext
            .QueueJobs.Where(job =>
                job.Queue == QueueNames.Music && job.Payload.Contains("MusicAnalysisJob")
            )
            .CountAsync();

        // Its own context, not the scoped one this controller is handed. The
        // shared instance is already serving the request, and counting on it
        // under concurrent load fails intermittently — green alone, 500 in a
        // full parallel suite.
        await using MediaContext analysisContext = await mediaContextFactory.CreateDbContextAsync();

        int analyzed = await analysisContext.TrackAudioAnalysis.CountAsync(analysis =>
            analysis.State == AudioAnalysisState.Ok
        );

        int failed = await analysisContext.TrackAudioAnalysis.CountAsync(analysis =>
            analysis.State == AudioAnalysisState.Failed
        );

        return Ok(
            new AudioAnalysisStatusDto
            {
                Paused = paused,
                Queued = queued,
                Analyzed = analyzed,
                Failed = failed,
            }
        );
    }

    /// <summary>
    /// Paused state, or false when the runner cannot answer.
    /// <para>
    /// <see cref="QueueRunner.Current" /> is a process-global that outlives the
    /// host which built it, so it can be non-null while holding a disposed
    /// service provider — reading it then throws
    /// <see cref="ObjectDisposedException" />. A read-only status endpoint must
    /// not fail because the queue runner is mid-teardown; "not paused" is the
    /// same answer it already gives when there is no runner at all.
    /// </para>
    /// </summary>
    private static bool IsQueuePausedOrFalse(string queue)
    {
        QueueRunner? runner = QueueRunner.Current;

        if (runner is null)
            return false;

        try
        {
            return runner.IsPaused(queue);
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Every queue an encode occupies, because pausing is a promise about the
    /// machine and not about one table.
    /// <para>Video is one job/one row now — <c>encoder</c> both decomposes and
    /// runs every bundle's ffmpeg inline, so pausing it already reaches the
    /// process. <c>encoder-gpu</c>/<c>encoder-cpu</c> stay listed for the music
    /// encoder, which still runs on its own lane. <c>encoder-task</c> is NOT
    /// listed: it has no worker pool anymore (nothing is ever dispatched to
    /// it), and <see cref="QueueRunner"/> indexes its worker dictionary
    /// directly — passing an unregistered queue name throws
    /// <see cref="KeyNotFoundException"/> rather than no-op'ing.</para>
    /// </summary>
    private static readonly string[] EncoderQueueFamily =
    [
        QueueNames.Encoder,
        QueueNames.EncoderGpu,
        QueueNames.EncoderCpu,
    ];

    /// <summary>
    /// Suspends every ffmpeg the encoder currently has running.
    /// <para>Stopping the workers only stops the NEXT job being reserved. An
    /// encode already inside ffmpeg carried on, and a single file can be an
    /// hour of it — so "paused" meant the machine stayed at full tilt and the
    /// operator watched a task complete two hundred seconds after they asked
    /// for quiet. Pause has to reach the process, which is what the per-task
    /// pause on this controller has always done; this is the same mechanism
    /// applied to everything at once.</para>
    /// <para>Suspended, not killed. The process keeps its place and its partial
    /// output, so resuming continues the encode rather than starting it
    /// again.</para>
    /// </summary>
    private void SuspendRunningEncodes()
    {
        foreach (int jobId in processRegistry.ActiveJobIds)
        foreach (int processId in processRegistry.GetProcessIds(jobId))
            processThrottle.Suspend(processId);
    }

    /// <summary>
    /// Wakes what <see cref="SuspendRunningEncodes"/> put to sleep. Runs before
    /// the workers restart so a resumed encode is never racing a newly reserved
    /// one for the same hardware.
    /// </summary>
    private void ResumeRunningEncodes()
    {
        foreach (int jobId in processRegistry.ActiveJobIds)
        foreach (int processId in processRegistry.GetProcessIds(jobId))
            processThrottle.Resume(processId);
    }

    /// <summary>Source-of-truth paused state for the encoder queue. The
    /// dashboard reads this on every queue refresh so its pause/resume
    /// toggle reflects the persisted state across server restarts —
    /// previously the UI tracked it in a client-side ref that defaulted to
    /// "running" no matter what the server thought.</summary>
    [HttpGet]
    [Route("queue/status")]
    public async Task<IActionResult> EncoderQueueStatus()
    {
        QueueRunner? runner = QueueRunner.Current;
        bool paused = runner is not null && EncoderQueueFamily.All(queue => runner.IsPaused(queue));

        await using QueueContext queueContext = await queueContextFactory.CreateDbContextAsync();

        // What is actually in the queue, which the listing cannot say. That
        // endpoint returns a bounded sample — a share of each priority band —
        // so a panel counting the cards it was handed announced 38 queued
        // encodes with 1,200 waiting. The depth is a count, it costs one query,
        // and it is the number the operator is reading the panel for.
        List<QueueTypeCount> counts = await queueContext
            .Database.SqlQueryRaw<QueueTypeCount>(
                """
                SELECT CASE
                         WHEN Payload LIKE '%VideoEncodeJob%' THEN 'video'
                         WHEN Payload LIKE '%MusicEncodeJob%' THEN 'music'
                         ELSE 'maintenance'
                       END AS Kind,
                       SUM(CASE WHEN ReservedAt IS NULL THEN 1 ELSE 0 END) AS Pending,
                       SUM(CASE WHEN ReservedAt IS NULL THEN 0 ELSE 1 END) AS Running
                FROM QueueJobs
                WHERE Queue = 'encoder'
                   OR (Queue = 'encoder-cpu' AND Payload LIKE '%MusicEncodeJob%')
                GROUP BY Kind
                """
            )
            .ToListAsync();

        // An album is one card, so it is one unit of queued work here too —
        // counting its tracks would put the panel's heading and its cards in
        // open disagreement.
        int albums = (await CountQueuedTracksByRelease(queueContext)).Count;

        int videoPending = counts.FirstOrDefault(row => row.Kind == "video")?.Pending ?? 0;
        int maintenancePending =
            counts.FirstOrDefault(row => row.Kind == "maintenance")?.Pending ?? 0;

        return Ok(
            new DataResponseDto<object>
            {
                Data = new
                {
                    paused,
                    pending = videoPending + maintenancePending + albums,
                    running = counts.Sum(row => row.Running),
                    video = videoPending,
                    music = albums,
                    maintenance = maintenancePending,
                },
            }
        );
    }

    /// <summary>
    /// Estimated completion time for the current encoder queue. Based on the
    /// rolling average duration of the most recent 50 successful encodes in
    /// EncodingHistory × remaining queue size. Returns zero when history is
    /// empty (no basis to extrapolate).
    /// </summary>
    [HttpGet]
    [Route("queue/eta")]
    public async Task<IActionResult> EncoderQueueEta()
    {
        List<EncodingHistory> recent = await historyRepository.GetRecentAsync(
            pageSize: 50,
            pageIndex: 0
        );

        await using QueueContext queueContext = await queueContextFactory.CreateDbContextAsync();
        int queueDepth = await queueContext.QueueJobs.CountAsync(j =>
            j.Queue == "encoder"
            || (j.Queue == "encoder-cpu" && j.Payload.Contains("MusicEncodeJob"))
        );

        if (recent.Count == 0 || queueDepth == 0)
        {
            return Ok(
                new
                {
                    queueDepth,
                    averageEncodeSeconds = 0.0,
                    estimatedSecondsRemaining = 0.0,
                    basedOnSamples = recent.Count,
                }
            );
        }

        double avgSeconds = recent.Average(h => h.DurationSeconds);
        double etaSeconds = avgSeconds * queueDepth;

        return Ok(
            new
            {
                queueDepth,
                averageEncodeSeconds = avgSeconds,
                estimatedSecondsRemaining = etaSeconds,
                basedOnSamples = recent.Count,
            }
        );
    }

    /// <summary>
    /// Reorder pending items in a named queue to match the supplied ordered list of job IDs.
    /// Running items (ReservedAt != null) are never moved. IDs not found in the queue are
    /// silently ignored. Queue items whose IDs were not supplied are appended after the
    /// reordered block, preserving their relative order.
    /// </summary>
    [HttpPost]
    [Route("reorder")]
    public async Task<IActionResult> ReorderQueue([FromBody] ReorderQueueDto request)
    {
        await using QueueContext queueContext = await queueContextFactory.CreateDbContextAsync();

        bool queueExists = await queueContext.QueueJobs.AnyAsync(j => j.Queue == request.QueueName);

        if (!queueExists)
            return BadRequestResponse($"Queue '{request.QueueName}' does not exist");

        // Load ALL jobs for this queue so we can compute priorities without a second trip.
        List<QueueJob> allJobs = await queueContext
            .QueueJobs.Where(j => j.Queue == request.QueueName)
            .OrderByDescending(j => j.Priority)
            .ThenBy(j => j.CreatedAt)
            .ThenBy(j => j.Id)
            .ToListAsync();

        // Split into running (reserved) and pending.
        List<QueueJob> runningJobs = [.. allJobs.Where(j => j.ReservedAt != null)];
        List<QueueJob> pendingJobs = [.. allJobs.Where(j => j.ReservedAt == null)];

        // Build ordered list: requested IDs first (in request order), then the rest.
        HashSet<int> requestedSet = [.. request.OrderedJobIds];

        List<QueueJob> reordered =
        [
            .. request
                .OrderedJobIds.Select(id => pendingJobs.FirstOrDefault(j => j.Id == id))
                .Where(j => j is not null)
                .Cast<QueueJob>(),
            .. pendingJobs.Where(j => !requestedSet.Contains(j.Id)),
        ];

        // Assign descending priority values so the first item is dispatched first.
        // Start high enough to not collide with running jobs.
        int basePriority = reordered.Count;
        for (int i = 0; i < reordered.Count; i++)
            reordered[i].Priority = basePriority - i;

        await queueContext.SaveChangesAsync();

        // Return the new ordering of ALL jobs for the queue.
        List<QueueJob> resultJobs = [.. runningJobs, .. reordered];

        QueueJobDto[] result =
        [
            .. resultJobs.Select(j => new QueueJobDto
            {
                Id = j.Id,
                Priority = j.Priority,
                PayloadId = string.Empty,
                Status = j.ReservedAt != null ? "running" : "pending",
            }),
        ];

        return Ok(new DataResponseDto<QueueJobDto[]> { Data = result });
    }

    [HttpPost]
    [Route("failed/retry")]
    [Route("failed/retry/{id:long?}")]
    public async Task<IActionResult> RetryFailedJobs(long? id = null)
    {
        await using QueueContext queueContext = await queueContextFactory.CreateDbContextAsync();
        using EfQueueContextAdapter adapter = new(queueContext);
        JobQueue jobQueue = new(adapter);

        if (id.HasValue)
        {
            FailedJob? failedJob = await queueContext.FailedJobs.FindAsync(id.Value);
            if (failedJob == null)
                return NotFoundResponse("Failed job not found");
        }

        jobQueue.RetryFailedJobs(id);

        string message = id.HasValue
            ? "Failed job has been queued for retry"
            : "All failed jobs have been queued for retry";

        return Ok(new StatusResponseDto<string> { Message = message, Status = "success" });
    }

    [HttpGet]
    [Route("failed")]
    public async Task<IActionResult> GetFailedJobs()
    {
        await using QueueContext queueContext = await queueContextFactory.CreateDbContextAsync();

        List<FailedJob> failedJobs = await queueContext
            .FailedJobs.OrderByDescending(j => j.FailedAt)
            .ToListAsync();

        return Ok(new DataResponseDto<List<FailedJob>> { Data = failedJobs });
    }

    [HttpGet]
    [Route("queue/incomplete")]
    public async Task<IActionResult> IncompleteEncodes()
    {
        List<IncompleteEncodeDto> rows = await mediaContext
            .IncompleteEncodes.AsNoTracking()
            .OrderByDescending(r => r.LastSeenAt)
            .Select(r => new IncompleteEncodeDto
            {
                Id = r.Id,
                MediaId = r.MediaId,
                Title = r.Title,
                MissingRenditions = r.MissingRenditions.Split(
                    '\n',
                    StringSplitOptions.RemoveEmptyEntries
                ),
                LastError = r.LastError,
                AttemptsMade = r.AttemptsMade,
                LastSeenAt = r.LastSeenAt,
            })
            .ToListAsync();

        return Ok(new DataResponseDto<List<IncompleteEncodeDto>> { Data = rows });
    }

    [HttpPost]
    [Route("queue/incomplete/{id:int}/retry")]
    public async Task<IActionResult> RetryIncompleteEncode(int id)
    {
        IncompleteEncode? row = await mediaContext.IncompleteEncodes.FindAsync(id);
        if (row is null)
            return NotFoundResponse("Incomplete encode record not found");

        if (Ulid.TryParse(row.FolderId, out Ulid folderUlid))
        {
            // Resolve the library that owns this folder.
            FolderLibrary? folderLibrary = await mediaContext
                .FolderLibrary.AsNoTracking()
                .FirstOrDefaultAsync(fl => fl.FolderId == folderUlid);

            if (folderLibrary is not null)
            {
                try
                {
                    int mediaId = checked((int)row.MediaId);

                    // Pick the best available video file for this media item (movie or episode).
                    VideoFile? videoFile = await mediaContext
                        .VideoFiles.AsNoTracking()
                        .FirstOrDefaultAsync(vf =>
                            vf.MovieId == mediaId || vf.EpisodeId == mediaId
                        );

                    if (videoFile is not null)
                    {
                        string inputFile =
                            videoFile.HostFolder.TrimEnd('/')
                            + "/"
                            + videoFile.Filename.TrimStart('/');

                        try
                        {
                            MediaJobDispatcher dispatcher = new();
                            dispatcher.DispatchJob<VideoEncodeJob>(
                                folderLibrary.LibraryId,
                                folderLibrary.FolderId,
                                row.MediaId.ToString(),
                                inputFile
                            );
                        }
                        catch (InvalidOperationException)
                        {
                            // Queue not running (e.g. server restart in progress) — the
                            // quarantine row is removed regardless so the admin can retry
                            // again once the queue is back up.
                        }
                    }
                }
                catch (OverflowException)
                {
                    // MediaId exceeds int range — cannot safely look up VideoFile.
                    // Row is still removed so the admin can clear corrupt quarantine entries.
                }
            }
        }

        mediaContext.IncompleteEncodes.Remove(row);
        await mediaContext.SaveChangesAsync();

        return Ok(new StatusResponseDto<string> { Message = "Re-queued", Status = "success" });
    }

    [HttpDelete]
    [Route("queue/incomplete/{id:int}")]
    public async Task<IActionResult> DeleteIncompleteEncode(int id)
    {
        IncompleteEncode? row = await mediaContext.IncompleteEncodes.FindAsync(id);
        if (row is null)
            return NotFoundResponse("Incomplete encode record not found");

        mediaContext.IncompleteEncodes.Remove(row);
        await mediaContext.SaveChangesAsync();

        return Ok(
            new StatusResponseDto<string>
            {
                Message = "Incomplete encode record removed",
                Status = "success",
            }
        );
    }

    [HttpDelete]
    [Route("queue/incomplete")]
    public async Task<IActionResult> DeleteAllIncompleteEncodes()
    {
        int removedCount = await mediaContext.IncompleteEncodes.ExecuteDeleteAsync();

        return Ok(
            new StatusResponseDto<int>
            {
                Data = removedCount,
                Message = "Incomplete encode records removed",
                Status = "success",
            }
        );
    }
}

/// <summary>
/// A queue row paired with the job deserialized from its payload. The row is the
/// authority on scheduling state (id, priority, reservation); the payload only
/// describes the work that was requested.
/// </summary>
internal sealed record QueueJobEntry(QueueJob Row, VideoEncodeJob? Job);

/// <summary>
/// A queue row paired with the music encode read from its payload. The row owns
/// the scheduling state; the payload names the release and the track.
/// </summary>
internal sealed record MusicQueueRow(QueueJob Row, MusicEncodeJob Job);

/// <summary>
/// How much encoder work of one kind is waiting and how much is in flight,
/// counted over the whole queue rather than over the listing's sample.
/// </summary>
internal sealed class QueueTypeCount
{
    public string? Kind { get; set; }
    public int Pending { get; set; }
    public int Running { get; set; }
}

/// <summary>A release's artwork, for the card that stands for it.</summary>
internal sealed record AlbumCover(Guid Id, string? Cover);

/// <summary>
/// Tracks of one release still on the encoder queue, counted in SQL.
/// </summary>
internal sealed class ReleaseTrackCount
{
    public string? ReleaseId { get; set; }
    public int Remaining { get; set; }
}

/// <summary>
/// A maintenance row described as itself, alongside the media folder it works on.
/// The folder is kept separate from the DTO because it is not part of the
/// contract — it is the key the title and the still are looked up by.
/// </summary>
internal sealed record MaintenanceJob(QueueJobDto Dto, string HostFolder);

public class QueueJobDto
{
    [JsonProperty("id")]
    public int Id { get; set; }

    [JsonProperty("payload_id")]
    public string PayloadId { get; set; } = string.Empty;

    [JsonProperty("title")]
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// The item's own artwork — a movie backdrop or an episode still. Carried on
    /// the queue row, not only on progress events, so a card can show the title
    /// it is about from the moment the job is queued rather than waiting for the
    /// first frame of encoding to arrive.
    /// </summary>
    /// <para>Usually a bare provider path that the client roots at
    /// <c>/images/original</c>. A release's cover is not served from there — it
    /// lives under <c>/images/music</c> — so an album card states the rooted
    /// path instead, and a value already starting <c>/images/</c> is used as it
    /// stands.</para>
    [JsonProperty("backdrop")]
    public string? Backdrop { get; set; }

    [JsonProperty("type")]
    public string Type { get; set; } = string.Empty;

    [JsonProperty("status")]
    public string Status { get; set; } = string.Empty;

    [JsonProperty("input_file")]
    public string InputFile { get; set; } = string.Empty;

    [JsonProperty("profile")]
    public string? Profile { get; set; }

    /// <summary>
    /// What this encode is going to produce, from the preset it will run with.
    /// Null on maintenance rows, and on an encode whose preset no longer
    /// resolves — a client shows the rest of the card either way.
    /// </summary>
    [JsonProperty("plan")]
    public QueueJobPlanDto? Plan { get; set; }

    [JsonProperty("priority")]
    public int Priority { get; set; }

    /// <summary>
    /// What kind of work this row is, when it is not an encode: <c>preview</c>
    /// for a scrub-sheet rebuild, <c>subtitles</c> for an OCR backfill. Null on a
    /// real encode, which is what every existing client already assumes it is
    /// reading — so an older build ignores this and keeps working.
    ///
    /// <para>These share the encoder queue on purpose, so they must not be shown
    /// as encodes: they have no media id, no artwork and no progress, and pushing
    /// them through the encode shape produced cards for titles that did not
    /// exist. Filtering them out instead went too far the other way — the server
    /// spends hours on this work and the panel claimed nothing was running.</para>
    /// </summary>
    [JsonProperty("kind")]
    public string? Kind { get; set; }

    /// <summary>
    /// How far a card made of several jobs has got, as a percentage. An album is
    /// one card over many track encodes, and its progress is how many of them are
    /// done — the only progress a card like that can honestly report, because the
    /// per-track progress events key on a track and this card is not one.
    ///
    /// <para>Null on a single-job card, where live progress events are the
    /// authority and an older client already reads them. Additive, so a client
    /// that has never heard of it keeps working.</para>
    /// </summary>
    [JsonProperty("progress")]
    public double? Progress { get; set; }

    /// <summary>Jobs of this card already finished. Null when the card is one job.</summary>
    [JsonProperty("completed_items")]
    public int? CompletedItems { get; set; }

    /// <summary>Jobs this card covers in total. Null when the card is one job.</summary>
    [JsonProperty("total_items")]
    public int? TotalItems { get; set; }
}

public class PatchQueueItemDto
{
    [JsonProperty("priority")]
    public int Priority { get; set; }
}

public class ReorderQueueDto
{
    [JsonProperty("queue_name")]
    public string QueueName { get; set; } = string.Empty;

    [JsonProperty("ordered_job_ids")]
    public List<int> OrderedJobIds { get; set; } = [];
}

public class IncompleteEncodeDto
{
    [JsonProperty("id")]
    public int Id { get; set; }

    [JsonProperty("media_id")]
    public long MediaId { get; set; }

    [JsonProperty("title")]
    public string Title { get; set; } = string.Empty;

    [JsonProperty("missing_renditions")]
    public string[] MissingRenditions { get; set; } = [];

    [JsonProperty("last_error")]
    public string? LastError { get; set; }

    [JsonProperty("attempts_made")]
    public int AttemptsMade { get; set; }

    [JsonProperty("last_seen_at")]
    public DateTime LastSeenAt { get; set; }
}

/// <summary>
/// Active task-worker count for the dashboard's pause toggle. Read directly
/// off <c>response.data</c> by the client, not through a
/// <see cref="DataResponseDto{T}"/> wrapper.
/// </summary>
public class RunnersResponseDto
{
    [JsonProperty("status")]
    public string Status { get; set; } = string.Empty;

    [JsonProperty("workers")]
    public int Workers { get; set; }
}
