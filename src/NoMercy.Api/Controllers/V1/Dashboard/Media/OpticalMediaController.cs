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

using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NoMercy.Authorization;
using NoMercy.DiscFormat.Abstractions.Disc;
using NoMercy.DiscFormat.Composition;
using NoMercy.DiscFormat.Disc.Bdmv;
using NoMercy.Encoder.Analysis;
using NoMercy.Encoder.LiveTranscode;
using NoMercy.NmSystem.Dto;
using NoMercy.NmSystem.Information;
using NoMercy.NmSystem.SystemCalls;
using NoMercy.OpticalMedia.Drives;
using NoMercy.OpticalMedia.Live;
using NoMercy.OpticalMedia.Metadata;
using NoMercy.OpticalMedia.Rip;
using NoMercy.OpticalMedia.Sources;
using NoMercy.Storage;

namespace NoMercy.Api.Controllers.V1.Dashboard.Media;

[ApiController]
[Tags("Dashboard Optical")]
[ApiVersion(1.0)]
// No class-level policy: read-only endpoints (drives/probe/title/play/stop)
// accept OpticalAccess — a lighter permission a household member can hold
// without full library access — while every write/config endpoint below
// (open/close/process/confirm/rip) is individually Moderator-gated.
[Authorize]
[Route("api/v{version:apiVersion}/dashboard/optical")]
public class OpticalMediaController(
    DiscSourceFactory discSourceFactory,
    DiscIdentificationService identificationService,
    IDriveMonitor driveMonitor,
    IStorageDriver localStorageDriver,
    ILiveDiscSession liveDiscSession,
    ILiveStreamingService liveStreamingService,
    ISessionManager sessionManager,
    IDiscSessionRegistry discSessionRegistry,
    DiscIdentityDispatcher discIdentityDispatcher,
    IDiscConfirmationService discConfirmationService,
    IDiscRipPreparationService discRipPreparationService,
    NoMercyQueue.Core.Interfaces.IJobDispatcher jobDispatcher
) : BaseController
{
    // ── Legacy endpoints (re-pointed to Module A) ──────────────────────────

    [HttpGet("drives")]
    [Authorize(Policy = "OpticalAccess")]
    public IActionResult GetOpticalDrives()
    {
        IEnumerable<object> drives = driveMonitor
            .GetDrives()
            .Select(d => new
            {
                path = d.Path.TrimEnd(Path.DirectorySeparatorChar),
                label = d.Label,
                open = !d.HasDisc,
                has_disc = d.HasDisc,
                disc_type = d.DiscType.ToString().ToLowerInvariant(),
            });

        return Ok(drives);
    }

    [HttpGet("{drivePath}")]
    [Authorize(Policy = "OpticalAccess")]
    public async Task<IActionResult> GetDriveContents(string drivePath, CancellationToken ct)
    {
        DiscDrive? drive = FindDrive(drivePath);
        if (drive is null)
            return NotFoundResponse($"No optical drive found at {drivePath}");

        if (!drive.HasDisc)
            return Ok(
                new
                {
                    path = drive.Path.TrimEnd(Path.DirectorySeparatorChar),
                    label = drive.Label,
                    open = true,
                    has_disc = false,
                    disc_type = drive.DiscType.ToString().ToLowerInvariant(),
                }
            );

        IDiscSource? source = discSourceFactory.CreateFor(drive.DiscType);
        if (source is null)
            return Ok(
                new
                {
                    path = drive.Path.TrimEnd(Path.DirectorySeparatorChar),
                    label = drive.Label,
                    open = false,
                    has_disc = true,
                    disc_type = drive.DiscType.ToString().ToLowerInvariant(),
                }
            );

        DiscInfo info = await source.ProbeAsync(drive, ct);

        return Ok(
            new
            {
                path = drive.Path.TrimEnd(Path.DirectorySeparatorChar),
                label = info.DiscTitle ?? info.DiscLabel ?? drive.Label,
                open = false,
                has_disc = true,
                disc_type = drive.DiscType.ToString().ToLowerInvariant(),
                disc = ProjectDiscInfo(info),
            }
        );
    }

    [HttpPost("{drivePath}/process")]
    [Authorize(Policy = "Moderator")]
    public IActionResult ProcessMedia(string drivePath)
    {
        if (string.IsNullOrWhiteSpace(drivePath))
            return BadRequestResponse("Drive path is required");

        // Legacy stub — callers should use the /rip endpoint instead.
        // The old Module B ProcessMedia had DVD/CD stubs that logged
        // "not yet implemented" and returned. That same behaviour is
        // preserved here without pulling in dead code.
        return Ok("Use /rip to start a rip job.");
    }

    [HttpPost("{drivePath}/open")]
    [Authorize(Policy = "Moderator")]
    public IActionResult OpenDrive(string drivePath)
    {
        if (string.IsNullOrWhiteSpace(drivePath))
            return BadRequestResponse("Drive path is required");

        bool success = Optical.OpenDrive(drivePath);

        if (!success)
            return BadRequestResponse("Failed to open drive");

        return Ok("Drive opened.");
    }

    [HttpPost("{drivePath}/close")]
    [Authorize(Policy = "Moderator")]
    public IActionResult CloseDrive(string drivePath)
    {
        if (string.IsNullOrWhiteSpace(drivePath))
            return BadRequestResponse("Drive path is required");

        bool success = Optical.CloseDrive(drivePath);

        if (!success)
            return BadRequestResponse("Failed to close drive");

        return Ok("Drive closed.");
    }

    [HttpPost("{drivePath}/play/{playlistId}")]
    [Authorize(Policy = "OpticalAccess")]
    public async Task<IActionResult> PlayMedia(
        string drivePath,
        string playlistId,
        [FromBody] PlayMediaRequest? request,
        CancellationToken ct
    )
    {
        if (string.IsNullOrWhiteSpace(drivePath))
            return BadRequestResponse("Drive path is required");

        if (string.IsNullOrWhiteSpace(playlistId))
            return BadRequestResponse("Playlist ID is required");

        if (!int.TryParse(playlistId, out int titleIndex) || titleIndex < 0)
            return BadRequestResponse("playlistId must be a non-negative integer title index");

        DiscDrive? drive = FindDrive(drivePath);
        if (drive is null)
            return NotFoundResponse($"No optical drive found at {drivePath}");

        if (!drive.HasDisc)
            return BadRequestResponse($"No disc loaded in drive {drivePath}");

        if (!sessionManager.CanStartSession(User.UserId().ToString()))
            return ServiceUnavailableResponse("Maximum concurrent live sessions reached");

        // Absent AudioTracks (older client, or a caller that just wants the
        // default), fall back to the disc's first audio stream muxed into
        // the single media playlist — the pre-existing behaviour.
        AudioTrackSelection[] audioTracks = request?.AudioTracks ?? [];

        ILiveSession session;
        try
        {
            session = await liveDiscSession.StartAsync(
                drive,
                titleIndex,
                TimeSpan.Zero,
                preferredQuality: null,
                audioTracks,
                ct
            );
        }
        catch (InvalidOperationException ex)
        {
            return ServiceUnavailableResponse(ex.Message);
        }

        sessionManager.RegisterSession(session, User.UserId().ToString());
        discSessionRegistry.Register(
            drive.Path.TrimEnd(Path.DirectorySeparatorChar),
            session.SessionId
        );

        // Multiple selected audio tracks were spawned as separate renditions and
        // stamped onto the runtime by LiveDiscSession — route the client to the
        // master playlist so it sees every one; a single-track session keeps the
        // plain media playlist it always used.
        bool useMaster =
            liveStreamingService.TryGetRuntime(session.SessionId, out LiveRuntimeSession runtime)
            && runtime.AudioRenditions.Count > 0;
        string playlistUrl = useMaster
            ? $"/api/v1/streaming/live/sessions/{session.SessionId}/master.m3u8"
            : $"/api/v1/streaming/live/sessions/{session.SessionId}/playlist.m3u8";
        LiveQuality quality = session.CurrentQuality;

        return Ok(
            new
            {
                session_id = session.SessionId,
                playlist_url = playlistUrl,
                quality_id = quality.Id,
                quality_label = quality.Label,
            }
        );
    }

    [HttpPost("{drivePath}/stop")]
    [Authorize(Policy = "OpticalAccess")]
    public async Task<IActionResult> StopMedia(string drivePath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(drivePath))
            return BadRequestResponse("Drive path is required");

        string normalised = drivePath.TrimEnd(Path.DirectorySeparatorChar);

        if (!discSessionRegistry.TryGet(normalised, out string sessionId))
            return NotFoundResponse($"No active live session for drive {drivePath}");

        await liveStreamingService.RemoveAsync(sessionId);
        sessionManager.RemoveSession(sessionId);
        discSessionRegistry.Remove(normalised);

        return NoContent();
    }

    // ── New endpoints (Module A, unchanged) ───────────────────────────────

    /// <summary>
    /// New full probe via NoMercy.OpticalMedia: enumerates every playlist
    /// on the disc plus all viable TMDB candidates with confidence scores.
    /// Replaces the legacy single-title <c>GetDriveContents</c> response
    /// for callers that want to render a multi-title browse UI or pick
    /// between metadata candidates.
    /// </summary>
    [HttpGet("{drivePath}/probe")]
    [Authorize(Policy = "OpticalAccess")]
    public async Task<IActionResult> ProbeDisc(string drivePath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(drivePath))
            return BadRequestResponse("Drive path is required");

        DiscDrive? drive = FindDrive(drivePath);

        if (drive is null)
            return NotFoundResponse($"No optical drive found at {drivePath}");

        if (!drive.HasDisc || drive.DiscType == OpticalDiscType.None)
            return Ok(
                new
                {
                    drive_path = drive.Path,
                    label = drive.Label,
                    has_disc = false,
                    disc_type = drive.DiscType.ToString().ToLowerInvariant(),
                }
            );

        IDiscSource? source = discSourceFactory.CreateFor(drive.DiscType);
        if (source is null)
            return BadRequestResponse($"No reader registered for disc type {drive.DiscType} (yet)");

        DiscInfo info = await source.ProbeAsync(drive, ct);
        DiscIdentification identification = await identificationService.IdentifyAsync(info, ct);
        string? discIdentity = TryComputeDiscIdentity(drive, info);

        return Ok(
            new
            {
                drive_path = drive.Path,
                label = info.DiscTitle ?? info.DiscLabel ?? drive.Label,
                has_disc = true,
                disc_type = drive.DiscType.ToString().ToLowerInvariant(),
                disc = ProjectDiscInfo(info),
                candidates = identification.Candidates.Select(ProjectCandidate),
                disc_identity = discIdentity,
            }
        );
    }

    /// <summary>
    /// Detailed streams + chapters for one title on the disc in the given
    /// drive. <see cref="ProbeDisc"/> only enumerates titles cheaply (name,
    /// duration) — this is the per-title call the dashboard needs before it
    /// can show real chapter counts or let the user pick audio/subtitle
    /// tracks for a rip.
    /// </summary>
    [HttpGet("{drivePath}/title/{titleIndex:int}")]
    [Authorize(Policy = "OpticalAccess")]
    public async Task<IActionResult> ProbeTitle(
        string drivePath,
        int titleIndex,
        CancellationToken ct
    )
    {
        if (string.IsNullOrWhiteSpace(drivePath))
            return BadRequestResponse("Drive path is required");

        if (titleIndex < 0)
            return BadRequestResponse("titleIndex must be a non-negative integer");

        DiscDrive? drive = FindDrive(drivePath);
        if (drive is null)
            return NotFoundResponse($"No optical drive found at {drivePath}");

        if (!drive.HasDisc)
            return BadRequestResponse($"No disc loaded in drive {drivePath}");

        IDiscSource? source = discSourceFactory.CreateFor(drive.DiscType);
        if (source is null)
            return BadRequestResponse($"No reader registered for disc type {drive.DiscType} (yet)");

        DiscTitle title = await source.ProbeTitleAsync(drive, titleIndex, ct);

        // "main_feature" vs "extra" now mirrors the pre-existing
        // is_main_feature field on the same title object instead of a
        // separate duration ranking, so the two fields can never disagree
        // in one response — no whole-disc re-probe needed per title.
        string kind = title.IsMainFeature ? "main_feature" : "extra";

        IReadOnlyList<double> chapterSeconds = ReadCatalogChapterSeconds(
            drive,
            drive.DiscType,
            titleIndex,
            title
        );

        return Ok(ProjectTitle(title, chapterSeconds, kind));
    }

    /// <summary>
    /// Returns ranked TMDB candidates for the disc in the given drive.
    /// No side effects — purely read-only preview. Returns the top 5
    /// candidates ordered by confidence (highest first) so the dashboard
    /// can render a candidate picker before the user confirms.
    /// </summary>
    [HttpPost("{drivePath}/resolve")]
    [Authorize(Policy = "OpticalAccess")]
    public async Task<IActionResult> ResolveDisc(string drivePath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(drivePath))
            return BadRequestResponse("Drive path is required");

        DiscDrive? drive = FindDrive(drivePath);

        if (drive is null || !drive.HasDisc)
            return NotFoundResponse($"No disc loaded in {drivePath}");

        IDiscSource? source = discSourceFactory.CreateFor(drive.DiscType);
        if (source is null)
            return BadRequestResponse($"No reader registered for disc type {drive.DiscType}");

        DiscInfo info = await source.ProbeAsync(drive, ct);
        DiscIdentification identification = await identificationService.IdentifyAsync(info, ct);

        return Ok(
            new
            {
                drive_path = drive.Path,
                disc_duration_sec = info.MainTitleDurationSec,
                needs_manual = identification.NeedsManualAssignment,
                auto_apply = identification.AutoApply,
                candidates = identification.Candidates.Take(5).Select(ProjectCandidate),
            }
        );
    }

    // ── Snake_case response projections ───────────────────────────────────
    // NoMercy.Encoder.Analysis's stream/chapter records (VideoStreamInfo,
    // AudioStreamInfo, SubtitleStreamInfo, ChapterInfo) are shared with the
    // encoder pipeline and serialize camelCase by default — reshaping them
    // globally would change output for unrelated endpoints. These project
    // only the optical-media API surface to the snake_case shape every other
    // dashboard endpoint already uses (see GetOpticalDrives above).

    private static object ProjectCandidate(DiscCandidate c) =>
        new
        {
            stable_id = c.StableId,
            media_type = c.Type?.ToString().ToLowerInvariant(),
            title = c.Title,
            year = c.Year,
            confidence = c.Confidence,
            poster_url = c.PosterUrl,
            backdrop_url = c.BackdropUrl,
            season_number = c.SeasonNumber,
            episode_number = c.EpisodeNumber,
        };

    private static object ProjectChapter(ChapterInfo c, int index) =>
        new
        {
            number = index + 1,
            timestamp = c.Start.ToString(@"hh\:mm\:ss"),
            title = c.Title,
        };

    /// <summary>
    /// Projects a title for the <c>/probe</c> disc-level listing, where no
    /// disc-content-catalog chapter marks or cross-title duration ranking
    /// have been computed (that only happens for the single title
    /// <see cref="ProbeTitle"/> fetches in detail). Kept separate from the
    /// per-title overload below so <c>/probe</c> stays the cheap call it's
    /// documented as.
    /// </summary>
    private static object ProjectTitle(DiscTitle t) =>
        new
        {
            index = t.Index,
            name = t.Name,
            duration = t.Duration.ToString(@"hh\:mm\:ss"),
            video_streams = t.VideoStreams.Select(v => new
            {
                codec = v.Codec,
                width = v.Width,
                height = v.Height,
                frame_rate = v.FrameRate,
                bit_rate = v.BitRateKbps,
            }),
            audio_streams = t.AudioStreams.Select(a => new
            {
                codec = a.Codec,
                channels = a.Channels,
                sample_rate = a.SampleRate,
                language = a.Language,
            }),
            subtitles = t.Subtitles.Select(s => new
            {
                codec = s.Codec,
                language = s.Language,
                forced = s.IsForced,
            }),
            chapters = t.Chapters.Select(ProjectChapter),
            estimated_size_bytes = t.EstimatedSizeBytes,
            is_main_feature = t.IsMainFeature,
        };

    /// <summary>
    /// Projects the detailed single-title probe. Adds two new fields on top
    /// of the existing shape — <c>chapter_marks</c> (the disc's real
    /// chapter-mark seconds, from <see cref="DiscContentCatalog"/> on
    /// Blu-ray or the analyzer's own chapter marks elsewhere) and
    /// <c>kind</c> (the disc-wide duration ranking computed by the caller).
    /// The existing <c>chapters</c> key keeps its original
    /// <c>{number,timestamp,title}</c> shape and <c>name</c> keeps returning
    /// <see cref="DiscTitle.Name"/> unchanged — compat-gate confirmed
    /// nomercy-app-web reads both today (<c>ChapterInfo</c>/
    /// <c>DiscTitleInfo</c> in <c>src/types/api/dashboard/ripper.ts</c>), so
    /// reshaping or nulling them here would break that live client. The
    /// "no disc format carries a real title name" fact from Task 1 is
    /// therefore not surfaced as always-null on this shared key; a future
    /// slice can add it under its own field name if a client needs it.
    /// </summary>
    private static object ProjectTitle(
        DiscTitle t,
        IReadOnlyList<double> chapterSeconds,
        string kind
    ) =>
        new
        {
            index = t.Index,
            name = t.Name,
            duration = t.Duration.ToString(@"hh\:mm\:ss"),
            video_streams = t.VideoStreams.Select(v => new
            {
                codec = v.Codec,
                width = v.Width,
                height = v.Height,
                frame_rate = v.FrameRate,
                bit_rate = v.BitRateKbps,
            }),
            audio_streams = t.AudioStreams.Select(a => new
            {
                codec = a.Codec,
                channels = a.Channels,
                sample_rate = a.SampleRate,
                language = a.Language,
            }),
            subtitles = t.Subtitles.Select(s => new
            {
                codec = s.Codec,
                language = s.Language,
                forced = s.IsForced,
            }),
            chapters = t.Chapters.Select(ProjectChapter),
            chapter_marks = chapterSeconds.Select(seconds => new { time_seconds = seconds }),
            estimated_size_bytes = t.EstimatedSizeBytes,
            is_main_feature = t.IsMainFeature,
            kind,
        };

    private static object ProjectDiscInfo(DiscInfo info) =>
        new
        {
            type = info.Type.ToString().ToLowerInvariant(),
            disc_label = info.DiscLabel,
            disc_title = info.DiscTitle,
            titles = info.Titles.Select(ProjectTitle),
            audio_tracks = info.AudioTracks?.Select(t => new
            {
                index = t.Index,
                title = t.Title,
                artist = t.Artist,
                duration = t.Duration.ToString(@"hh\:mm\:ss"),
                sample_rate = t.SampleRate,
                channels = t.Channels,
            }),
            total_duration = info.TotalDuration.ToString(@"hh\:mm\:ss"),
            protection = info.Protection is null
                ? null
                : new
                {
                    kind = info.Protection.Kind,
                    volume_id = info.Protection.VolumeId,
                    message = info.Protection.Message,
                },
            main_title_duration_sec = info.MainTitleDurationSec,
        };

    /// <summary>
    /// Applies the user's chosen TMDB match to the rip output. Renames/moves
    /// the raw rip file into the canonical media-library path and triggers a
    /// library refresh so the file is picked up by the import pipeline.
    /// </summary>
    [HttpPost("{drivePath}/confirm")]
    [Authorize(Policy = "Moderator")]
    public async Task<IActionResult> ConfirmDisc(
        string drivePath,
        [FromBody] DiscConfirmRequest request,
        CancellationToken ct
    )
    {
        if (string.IsNullOrWhiteSpace(drivePath))
            return BadRequestResponse("Drive path is required");

        if (string.IsNullOrWhiteSpace(request.TmdbId))
            return BadRequestResponse("TmdbId is required");

        if (string.IsNullOrWhiteSpace(request.RipOutputPath))
            return BadRequestResponse("RipOutputPath is required");

        string ripStagingRoot = Path.Combine(AppFiles.TranscodePath, "ripper");
        if (!RipStagingPath.IsWithinStaging(request.RipOutputPath, ripStagingRoot))
            return BadRequestResponse(
                "RipOutputPath must be inside the server rip staging directory"
            );

        if (!localStorageDriver.FileExists(request.RipOutputPath))
            return NotFoundResponse($"Rip output not found at {request.RipOutputPath}");

        CustomMetadata meta =
            request.MediaType == "tv"
                ? new(
                    Title: request.Title ?? string.Empty,
                    Year: request.Year,
                    Type: MediaType.TvShow,
                    PosterUrl: request.PosterUrl,
                    SeasonNumber: request.SeasonNumber ?? 1,
                    EpisodeStartNumber: request.EpisodeNumber ?? 1
                )
                : new CustomMetadata(
                    Title: request.Title ?? string.Empty,
                    Year: request.Year,
                    Type: MediaType.Movie,
                    PosterUrl: request.PosterUrl
                );

        DiscConfirmationResult result = await discConfirmationService.ConfirmAsync(
            request.FolderId,
            request.LibraryId,
            request.RipOutputPath,
            meta,
            ct
        );

        switch (result.Outcome)
        {
            case DiscConfirmationOutcome.FolderNotFound:
                return BadRequestResponse(
                    $"FolderId {request.FolderId} does not match any library folder"
                );
            case DiscConfirmationOutcome.LibraryNotFound:
                return BadRequestResponse(
                    $"LibraryId {request.LibraryId} does not match any library"
                );
        }

        return Ok(
            new
            {
                tmdb_id = request.TmdbId,
                media_type = request.MediaType,
                destination = result.Destination,
                library_refresh_triggered = true,
            }
        );
    }

    /// <summary>
    /// Enqueues a durable <see cref="DiscRipJob"/> for the requested titles.
    /// Returns the job id the caller can use to poll rip progress on the
    /// <c>ripperHub</c>. Fails fast on DRM-locked discs and missing
    /// destination folders so the background job never starts in a doomed state.
    /// </summary>
    [HttpPost("{drivePath}/rip")]
    [Authorize(Policy = "Moderator")]
    public async Task<IActionResult> RipDisc(
        string drivePath,
        [FromBody] RipRequest request,
        CancellationToken ct
    )
    {
        if (string.IsNullOrWhiteSpace(drivePath))
            return BadRequestResponse("Drive path is required");

        DiscDrive? drive = FindDrive(drivePath);

        if (drive is null || !drive.HasDisc)
            return NotFoundResponse($"No disc loaded in {drivePath}");

        // For CD discs, an empty SelectedTitleIndices is fine — the endpoint
        // defaults to all probed tracks further below. Video discs must supply at least one.
        if (request.SelectedTitleIndices.Length == 0 && drive.DiscType != OpticalDiscType.Cd)
            return BadRequestResponse("At least one title must be selected");

        DiscRipPreparationResult prepared = await discRipPreparationService.PrepareAsync(
            drive,
            request,
            ct
        );

        if (!prepared.Success)
            return BadRequestResponse(prepared.ErrorMessage!);

        DiscRipJob job = new(
            prepared.EnrichedRequest!,
            prepared.OutputDir!,
            prepared.TargetFolderId,
            prepared.TargetLibraryId,
            prepared.TargetLibraryType
        );

        jobDispatcher.Dispatch(job, job.QueueName, job.Priority);

        return Accepted(
            new
            {
                job_id = job.JobId,
                drive_path = drive.Path,
                output_dir = prepared.OutputDir,
                titles_queued = request.SelectedTitleIndices.Length,
                mode = request.Mode.ToString(),
            }
        );
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    /// <summary>
    /// Real chapter-mark seconds for one title, sourced from
    /// <see cref="DiscContentCatalog"/> — the vendored parser that reads a
    /// Blu-ray playlist's own entry marks, never estimated or driven. Only
    /// Blu-ray carries an .mpls to build the catalog from; DVD/CD titles
    /// fall back to whatever chapter marks the per-title stream probe
    /// already found (real ffprobe chapters, just not catalog-sourced) so
    /// the field is never silently empty on formats Task 1 didn't cover.
    /// </summary>
    private IReadOnlyList<double> ReadCatalogChapterSeconds(
        DiscDrive drive,
        OpticalDiscType discType,
        int titleIndex,
        DiscTitle title
    )
    {
        if (discType != OpticalDiscType.BluRay)
            return [.. title.Chapters.Select(c => c.Start.TotalSeconds)];

        try
        {
            string trimmed = drive.Path.TrimEnd('\\', '/');
            string mplsPath = Path.Combine(trimmed, "BDMV", "PLAYLIST", $"{titleIndex:D5}.mpls");

            if (!localStorageDriver.FileExists(mplsPath))
                return [.. title.Chapters.Select(c => c.Start.TotalSeconds)];

            using Stream stream = localStorageDriver.OpenRead(mplsPath);
            using MemoryStream buffer = new();
            stream.CopyTo(buffer);
            MplsPlaylist playlist = MplsParser.Parse(buffer.ToArray());

            IReadOnlyList<DiscContentTitle> catalog = DiscContentCatalog.Build(
                new Dictionary<int, MplsPlaylist> { [titleIndex] = playlist }
            );
            DiscContentTitle? catalogued = catalog.FirstOrDefault(c => c.Playlist == titleIndex);

            return catalogued is null || catalogued.ChapterTimes.Count == 0
                ? [.. title.Chapters.Select(c => c.Start.TotalSeconds)]
                : catalogued.ChapterTimes;
        }
        // Catalog enrichment is best-effort, same as the existing mpls
        // language merge in BlurayDiscSource — a malformed/unreadable mpls
        // must never fail the title probe. Bounded to the failure modes
        // MplsParser/BigEndianReader and the storage read can genuinely
        // produce for a truncated/corrupt/unreadable playlist file:
        // InvalidDataException (bad MPLS signature), IndexOutOfRangeException
        // and ArgumentOutOfRangeException (BigEndianReader/Span reads past
        // the end of a truncated buffer), and IOException/
        // UnauthorizedAccessException from the storage-driver file read.
        // OperationCanceledException and anything else propagate.
        catch (Exception ex)
            when (ex
                    is InvalidDataException
                        or IndexOutOfRangeException
                        or ArgumentOutOfRangeException
                        or IOException
                        or UnauthorizedAccessException
            )
        {
            return [.. title.Chapters.Select(c => c.Start.TotalSeconds)];
        }
    }

    /// <summary>
    /// Disc structural identity via <see cref="DiscIdentityDispatcher"/>, for
    /// disc kinds the dispatcher has a reader for. Best-effort — an
    /// unsupported disc kind (CD) or a read failure (locked drive, missing
    /// IFO/BDMV structure) yields null rather than failing the probe.
    /// </summary>
    private string? TryComputeDiscIdentity(DiscDrive drive, DiscInfo info)
    {
        // No identity reader exists for this disc kind (e.g. CD) — an
        // expected, non-exceptional case, not a failure to swallow.
        DiscKind? kind = drive.DiscType switch
        {
            OpticalDiscType.BluRay => DiscKind.Hdmv,
            OpticalDiscType.Dvd => DiscKind.Dvd,
            _ => null,
        };

        if (kind is null)
            return null;

        try
        {
            string discTitle = info.DiscTitle ?? info.DiscLabel ?? drive.Label ?? string.Empty;

            DiscTranspileRequest request =
                kind == DiscKind.Dvd
                    ? new DiscTranspileRequest
                    {
                        Kind = kind.Value,
                        DiscTitle = discTitle,
                        IfoFiles = ReadDvdIfoFiles(drive.Path),
                    }
                    : new DiscTranspileRequest
                    {
                        Kind = kind.Value,
                        DiscTitle = discTitle,
                        DevicePath = drive.Path,
                    };

            return discIdentityDispatcher.Read(request).Id;
        }
        // Best-effort, mirroring ReadCatalogChapterSeconds — an unreadable
        // drive or missing/malformed IFO/BDMV structure must never fail the
        // disc probe. Bounded to what the readers and libbluray genuinely
        // throw for those cases: InvalidOperationException (DvdIdentityReader
        // /BlurayIdentityReader/LibBlurayClient's own "no data"/native-open
        // failures) and NotSupportedException (dispatcher has no reader
        // registered for this kind, e.g. Blu-ray support absent on this
        // host), DllNotFoundException/EntryPointNotFoundException when the
        // libbluray native library itself isn't installed on this host,
        // plus IOException/UnauthorizedAccessException from reading the
        // DVD's IFO files off the storage driver. OperationCanceledException
        // and anything else propagate.
        catch (Exception ex)
            when (ex
                    is InvalidOperationException
                        or NotSupportedException
                        or DllNotFoundException
                        or EntryPointNotFoundException
                        or IOException
                        or UnauthorizedAccessException
            )
        {
            return null;
        }
    }

    private IReadOnlyDictionary<string, byte[]> ReadDvdIfoFiles(string drivePath)
    {
        string trimmed = drivePath.TrimEnd('\\', '/');
        string videoTsDir = Path.Combine(trimmed, "VIDEO_TS");

        if (!localStorageDriver.DirectoryExists(videoTsDir))
            return new Dictionary<string, byte[]>();

        Dictionary<string, byte[]> files = new(StringComparer.OrdinalIgnoreCase);
        foreach (
            string path in localStorageDriver.EnumerateFileSystemEntries(
                videoTsDir,
                "*.IFO",
                SearchOption.TopDirectoryOnly
            )
        )
        {
            using Stream stream = localStorageDriver.OpenRead(path);
            using MemoryStream buffer = new();
            stream.CopyTo(buffer);
            files[Path.GetFileName(path)] = buffer.ToArray();
        }

        return files;
    }

    private DiscDrive? FindDrive(string drivePath) =>
        driveMonitor
            .GetDrives()
            .FirstOrDefault(d =>
                d.Path.TrimEnd(Path.DirectorySeparatorChar)
                    .Equals(
                        drivePath.TrimEnd(Path.DirectorySeparatorChar),
                        StringComparison.OrdinalIgnoreCase
                    )
            );
}
