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

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NoMercy.Api.DTOs.Common;
using NoMercy.Api.DTOs.Music;
using NoMercy.Authorization;
using NoMercy.Data.Repositories;
using NoMercy.Data.Services.Music;
using NoMercy.Database.Models.Music;
using NoMercy.Events;
using NoMercy.Events.Library;
using NoMercy.Events.Music;
using NoMercy.NmSystem.Extensions;

namespace NoMercy.Api.Controllers.V1.Music;

[ApiController]
[Tags(tags: "Music Tracks")]
[Authorize(Policy = "MediaAccess")]
[Route("api/v{version:apiVersion}/music/tracks")]
public class TracksController : BaseController
{
    private readonly IMusicRepository _musicRepository;
    private readonly IEventBus _eventBus;
    private readonly LyricsResolver _lyricsResolver;
    private readonly LyricsRequestedNotifier _lyricsRequested;

    public TracksController(
        IMusicRepository musicService,
        IEventBus eventBus,
        LyricsResolver lyricsResolver,
        LyricsRequestedNotifier lyricsRequested
    )
    {
        _musicRepository = musicService;
        _eventBus = eventBus;
        _lyricsResolver = lyricsResolver;
        _lyricsRequested = lyricsRequested;
    }

    [HttpGet]
    public async Task<IActionResult> Index()
    {
        Guid userId = User.UserId();

        string language = Language();

        List<TrackUser> rawTracks = await _musicRepository.GetTracks(userId);
        List<ArtistTrackDto> tracks = rawTracks
            .Select(track => new ArtistTrackDto(track.Track, language))
            .ToList();

        if (tracks.Count == 0)
            return NotFoundResponse("Tracks not found");

        return Ok(
            new TracksResponseDto
            {
                Data = new()
                {
                    Name = "Favorite Tracks".Localize(),
                    Link = new("music/tracks", UriKind.Relative),
                    Type = "track",
                    ColorPalette = new(),
                    Tracks = tracks,
                },
            }
        );
    }

    /// <summary>
    /// Audio analysis for a set of tracks.
    /// <para>
    /// A dedicated route rather than a field on every track response: analysis
    /// is absent for most rows for most of a library's life, so inlining it
    /// would grow every list for every user to carry mostly nulls.
    /// </para>
    /// </summary>
    [HttpPost]
    [Route("analysis")]
    public async Task<IActionResult> Analysis([FromBody] TrackAudioAnalysisRequestDto request)
    {
        if (request.TrackIds.Count == 0)
            return BadRequestResponse("No track ids supplied");

        if (request.TrackIds.Count > TrackAudioAnalysisRequestDto.MaxTrackIds)
            return BadRequestResponse(
                $"At most {TrackAudioAnalysisRequestDto.MaxTrackIds} track ids per request"
            );

        Guid[] trackIds = request.TrackIds.Distinct().ToArray();

        TrackAnalysisBundle bundle = await _musicRepository.GetTrackAnalysisBundleAsync(trackIds);

        Dictionary<Guid, TrackDjAnalysis> djByTrackId = bundle.Dj.ToDictionary(row => row.TrackId);
        ILookup<Guid, TrackStem> stemsByTrackId = bundle.Stems.ToLookup(row => row.TrackId);

        List<TrackAudioAnalysisDto> data =
        [
            .. bundle.Analysis.Select(row => new TrackAudioAnalysisDto(row)
            {
                Dj = djByTrackId.TryGetValue(row.TrackId, out TrackDjAnalysis? dj)
                    ? new TrackDjAnalysisDto(dj, row.KeyName)
                    : null,
                Stems = [.. stemsByTrackId[row.TrackId].Select(stem => new TrackStemDto(stem))],
            }),
        ];

        return Ok(new TrackAudioAnalysisResponseDto { Data = data });
    }

    [HttpPost]
    [Route("{id:guid}/like")]
    public async Task<IActionResult> Value(Guid id, [FromBody] LikeRequestDto request)
    {
        Guid userId = User.UserId();

        Track? track = await _musicRepository.GetTrackWithIncludesAsync(id);

        if (track is null)
            return NotFoundResponse("Track not found");

        await _musicRepository.LikeTrackAsync(userId, track, request.Value);

        await _eventBus.PublishAsync(
            new LibraryRefreshedEvent
            {
                QueryKey = ["music", "album", track.AlbumTrack.FirstOrDefault()?.Album.Id],
            }
        );
        await _eventBus.PublishAsync(
            new LibraryRefreshedEvent
            {
                QueryKey = ["music", "artist", track.ArtistTrack.FirstOrDefault()?.Artist.Id],
            }
        );
        await _eventBus.PublishAsync(new LibraryRefreshedEvent { QueryKey = ["music", "tracks"] });

        await _eventBus.PublishAsync(
            new MusicItemLikedEvent
            {
                UserId = User.UserId(),
                ItemId = track.Id,
                ItemType = "track",
                Liked = request.Value,
            }
        );

        return Ok(
            new StatusResponseDto<string>
            {
                Status = "ok",
                Message = "{0} {1}",
                Args = new object[] { track.Name, request.Value ? "liked" : "unliked" },
            }
        );
    }

    /// <summary>
    /// The lyrics of a track. Every line time is final: the track offset is
    /// already applied, here and in every track DTO that embeds lyrics, so a
    /// client never shifts the times again. <c>offset</c> is informational, for
    /// the adjustment UI. <c>source</c> says where the text came from and drives
    /// attribution; <c>timing</c> says where the line timing came from. Both are
    /// null for lyrics stored before provenance was recorded.
    /// </summary>
    [HttpGet]
    [Route("{id:guid}/lyrics")]
    public async Task<IActionResult> Lyrics(Guid id)
    {
        Track? track = await _musicRepository.GetTrackWithIncludesAsync(id);

        if (track is null)
            return NotFoundResponse("Track not found");

        // Non-empty => real cached lyrics. Empty array => negative marker
        // (checked, none found) persisted by the resolver; treat as not found
        // rather than re-querying the rate-limited providers.
        if (track.Lyrics is { Length: > 0 })
            return Ok(
                await Served(track, new(track.Lyrics, track.LyricsSource, track.LyricsTiming))
            );
        if (track.Lyrics is not null)
        {
            await _lyricsRequested.NotifyAsync(id);
            return NotFoundResponse("Subtitle not found");
        }

        try
        {
            // Coalesced: concurrent requests from multiple devices for this
            // track share a single provider fetch instead of each hitting the
            // rate-limited Lrclib/Musixmatch queues.
            ResolvedLyrics? lyrics = await _lyricsResolver.ResolveAsync(id);
            if (lyrics is null)
            {
                await _lyricsRequested.NotifyAsync(id);
                return NotFoundResponse("Subtitle not found");
            }
            return Ok(await Served(track, lyrics));
        }
        catch (Exception e)
        {
            return NotFoundResponse(e.Message);
        }
    }

    // Pure: ShiftBy returns a new array and never mutates the input. The
    // coalescing LyricsResolver hands the same Lyric[] instance to every
    // concurrent caller, so in-place mutation here would double-apply the
    // offset per extra device.
    // Text without timing is the other cue for a plugin that generates it;
    // the topic is throttled per track by the notifier.
    private async Task<LyricsResponseDto> Served(Track track, ResolvedLyrics lyrics)
    {
        if (!lyrics.Lines.Any(line => line.Time.Total > 0))
            await _lyricsRequested.NotifyAsync(track.Id);
        return LyricsResponse(track, lyrics);
    }

    private static LyricsResponseDto LyricsResponse(Track track, ResolvedLyrics lyrics) =>
        new()
        {
            Data = LyricTimeline.ShiftBy(lyrics.Lines, track.LyricsOffset),
            Offset = track.LyricsOffset,
            Source = lyrics.Source,
            Timing = lyrics.Timing,
        };

    /// <summary>
    /// Sets or clears the lyrics offset of a track, in milliseconds (-30000 to
    /// 30000). The next <c>GET /lyrics</c> and every track DTO that embeds
    /// lyrics return the line times with the new offset applied.
    /// </summary>
    [HttpPatch]
    [Route("{id:guid}/lyrics-offset")]
    public async Task<IActionResult> LyricsOffset(Guid id, [FromBody] PatchLyricsOffsetDto request)
    {
        if (request.Offset is not null && (request.Offset < -30000 || request.Offset > 30000))
            return ValidationProblem("Offset must be between -30000 and 30000 ms");

        Track? track = await _musicRepository.GetTrackWithIncludesAsync(id);

        if (track is null)
            return NotFoundResponse("Track not found");

        await _musicRepository.UpdateTrackLyricsOffsetAsync(track, request.Offset);

        return Ok(
            new LyricsOffsetResponseDto
            {
                Status = "ok",
                Message = request.Offset is null
                    ? "Lyrics offset cleared"
                    : $"Lyrics offset set to {request.Offset} ms",
                Offset = request.Offset,
            }
        );
    }

    [HttpPost]
    [Route("{id:guid}/playback")]
    public async Task<IActionResult> Playback(Guid id)
    {
        Guid userId = User.UserId();

        Track? track = await _musicRepository.GetTrackAsync(id);

        if (track is null)
            return NotFoundResponse("Track not found");

        await _musicRepository.RecordPlaybackAsync(id, userId);

        return Ok(new StatusResponseDto<string> { Status = "ok", Message = "Playback recorded" });
    }
}
