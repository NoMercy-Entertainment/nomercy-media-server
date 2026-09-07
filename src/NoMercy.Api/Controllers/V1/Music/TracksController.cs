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
using Newtonsoft.Json;
using NoMercy.Api.DTOs.Common;
using NoMercy.Api.DTOs.Music;
using NoMercy.Api.Services.Music;
using NoMercy.Authorization;
using NoMercy.Data.Repositories;
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

    public TracksController(
        IMusicRepository musicService,
        IEventBus eventBus,
        LyricsResolver lyricsResolver
    )
    {
        _musicRepository = musicService;
        _eventBus = eventBus;
        _lyricsResolver = lyricsResolver;
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

        List<TrackAudioAnalysis> analysis = await _musicRepository.GetTrackAudioAnalysisAsync(
            request.TrackIds.Distinct().ToArray()
        );

        return Ok(
            new TrackAudioAnalysisResponseDto
            {
                Data = analysis.Select(row => new TrackAudioAnalysisDto(row)).ToList(),
            }
        );
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
                new LyricsResponseDto
                {
                    Data = ApplyLyricsOffset(track.Lyrics, track.LyricsOffset),
                    Offset = track.LyricsOffset,
                }
            );
        if (track.Lyrics is not null)
            return NotFoundResponse("Subtitle not found");

        try
        {
            // Coalesced: concurrent requests from multiple devices for this
            // track share a single provider fetch instead of each hitting the
            // rate-limited Lrclib/Musixmatch queues.
            Lyric[]? lyrics = await _lyricsResolver.ResolveAsync(id);
            if (lyrics is null)
                return NotFoundResponse("Subtitle not found");
            return Ok(
                new LyricsResponseDto
                {
                    Data = ApplyLyricsOffset(lyrics, track.LyricsOffset),
                    Offset = track.LyricsOffset,
                }
            );
        }
        catch (Exception e)
        {
            return NotFoundResponse(e.Message);
        }
    }

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

    // Pure: returns a new array and never mutates the input. The coalescing
    // LyricsResolver hands the same Lyric[] instance to every concurrent caller,
    // so in-place mutation here would double-apply the offset per extra device.
    private static Lyric[] ApplyLyricsOffset(Lyric[] lyrics, int? offsetMs)
    {
        if (offsetMs is null or 0)
            return lyrics;
        double offsetSec = offsetMs.Value / 1000.0;
        return lyrics
            .Select(line =>
            {
                double newTotal = Math.Max(0, line.Time.Total + offsetSec);
                int totalHundredths = (int)Math.Round(newTotal * 100);
                return new Lyric
                {
                    Text = line.Text,
                    Time = new()
                    {
                        Total = newTotal,
                        Minutes = totalHundredths / 6000,
                        Seconds = totalHundredths / 100 % 60,
                        Hundredths = totalHundredths % 100,
                    },
                };
            })
            .ToArray();
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

public record LyricsResponseDto
{
    [JsonProperty("data")]
    public Lyric[] Data { get; set; } = [];

    [JsonProperty("offset")]
    public int? Offset { get; set; }
}

public record PatchLyricsOffsetDto
{
    [JsonProperty("offset")]
    public int? Offset { get; set; }
}

public record LyricsOffsetResponseDto
{
    [JsonProperty("status")]
    public string Status { get; set; } = string.Empty;

    [JsonProperty("message")]
    public string Message { get; set; } = string.Empty;

    [JsonProperty("offset")]
    public int? Offset { get; set; }
}
