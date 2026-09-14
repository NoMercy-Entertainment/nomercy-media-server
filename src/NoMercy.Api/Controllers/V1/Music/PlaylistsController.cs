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
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using NoMercy.Api.DTOs.Common;
using NoMercy.Api.DTOs.Media.Components;
using NoMercy.Api.DTOs.Music;
using NoMercy.Authorization;
using NoMercy.Data.Repositories;
using NoMercy.Database.Models.Music;
using NoMercy.Events;
using NoMercy.Events.Library;
using NoMercy.MediaProcessing.Images;
using NoMercy.MediaProcessing.Jobs;
using NoMercy.MediaProcessing.Jobs.PaletteJobs;
using NoMercy.NmSystem.Extensions;
using NoMercy.NmSystem.Information;
using NoMercyQueue;

namespace NoMercy.Api.Controllers.V1.Music;

[ApiController]
[ApiVersion(1.0)]
[Tags("Music Playlists")]
[Authorize(Policy = "MediaAccess")]
[Route("api/v{version:apiVersion}/music/playlists", Order = 3)]
public class PlaylistsController : BaseController
{
    private readonly IMusicRepository _musicRepository;
    private readonly IEventBus _eventBus;
    private readonly IJobDispatcher _jobDispatcher;
    private readonly IMusicCoverStore _coverStore;

    private readonly ILogger<PlaylistsController> _logger;

    public PlaylistsController(
        ILogger<PlaylistsController> logger,
        IMusicRepository musicService,
        IEventBus eventBus,
        IJobDispatcher jobDispatcher,
        IMusicCoverStore coverStore
    )
    {
        _logger = logger;
        _musicRepository = musicService;
        _eventBus = eventBus;
        _jobDispatcher = jobDispatcher;
        _coverStore = coverStore;
    }

    [HttpGet]
    public async Task<IActionResult> Index()
    {
        Guid userId = User.UserId();
        if (!AuthPolicy.IsAllowed(User))
            return UnauthorizedResponse("You do not have permission to view playlists");

        List<PlaylistCardDto> playlistCards = await _musicRepository.GetPlaylistCardsAsync(userId);

        ComponentEnvelope response = Component
            .Grid()
            .WithItems(playlistCards.Select(p => Component.MusicCard(new MusicCardData(p))));

        return Ok(ComponentResponse.From(response));
    }

    [HttpGet]
    [Route("{id:guid}")]
    public async Task<IActionResult> Show(Guid id)
    {
        Guid userId = User.UserId();
        if (!AuthPolicy.IsAllowed(User))
            return UnauthorizedResponse("You do not have permission to view playlists");

        Playlist? playlist = await _musicRepository.GetPlaylistAsync(userId, id);

        if (playlist == null)
            return NotFoundResponse("Playlist not found");

        string language = Language();

        if (string.IsNullOrEmpty(playlist._colorPalette) || playlist._colorPalette == "{}")
            _jobDispatcher.QueueColorPaletteInBackground("playlist", playlist.Id.ToString());

        return Ok(new PlaylistResponseDto { Data = new(playlist, language) });
    }

    [HttpPost]
    [Authorize(Policy = "MediaAccess")]
    public async Task<IActionResult> Create([FromBody] CreatePlaylistRequestDto request)
    {
        Guid userId = User.UserId();

        if (await _musicRepository.PlaylistNameExistsAsync(request.Name, userId))
            return ConflictResponse("You already have a playlist with that name");

        Playlist newPlaylist = new()
        {
            Name = request.Name,
            Description = request.Description,
            UserId = userId,
        };

        string slug = newPlaylist.Name.ToSlug();

        if (request.Cover is not null)
        {
            byte[]? binData = ImageDataUri.Decode(request.Cover, out string? coverError);
            if (binData is null)
                return BadRequestResponse(coverError!);

            SavedMusicCover saved = await _coverStore.SaveAsync(slug, new MemoryStream(binData));
            newPlaylist.Cover = saved.Cover;
            newPlaylist._colorPalette = saved.ColorPalette;
        }

        _logger.LogInformation("{Playlist}", newPlaylist);

        await _musicRepository.CreatePlaylistAsync(newPlaylist, request.Tracks);

        Playlist? playlist = await _musicRepository.GetPlaylistByNameAsync(request.Name, userId);

        await _eventBus.PublishAsync(new LibraryRefreshedEvent { QueryKey = ["music-playlists"] });

        return Ok(new StatusResponseDto<Playlist?> { Data = playlist, Status = "ok" });
    }

    [HttpPatch]
    [Route("{id:guid}")]
    [Authorize(Policy = "MediaAccess")]
    public async Task<IActionResult> Edit(Guid id, [FromBody] CreatePlaylistRequestDto request)
    {
        Guid userId = User.UserId();
        Playlist? playlist = await _musicRepository.GetPlaylistForEditAsync(id, userId);

        if (playlist is null)
            return NotFoundResponse("Playlist not found");

        string slug = playlist.Name.ToSlug();
        string colorPalette = playlist._colorPalette.OrEmpty();
        string cover = playlist.Cover.OrEmpty();

        if (request.Cover is not null)
        {
            byte[]? binData = ImageDataUri.Decode(request.Cover, out string? coverError);
            if (binData is null)
                return BadRequestResponse(coverError!);

            SavedMusicCover saved = await _coverStore.SaveAsync(slug, new MemoryStream(binData));
            cover = saved.Cover;
            colorPalette = saved.ColorPalette;
        }

        int result = await _musicRepository.UpdatePlaylistMetadataAsync(
            id,
            userId,
            request.Name,
            request.Description,
            cover,
            colorPalette
        );

        await _eventBus.PublishAsync(
            new LibraryRefreshedEvent { QueryKey = ["music", "playlists", id] }
        );

        return Ok(
            new StatusResponseDto<string>
            {
                Data = (
                    result > 0 ? "Playlist updated successfully" : "No changes made"
                ).Localize(),
                Status = "ok",
            }
        );
    }

    [HttpDelete]
    [Route("{id:guid}")]
    [Authorize(Policy = "MediaAccess")]
    public async Task<IActionResult> Destroy(Guid id)
    {
        int result = await _musicRepository.DeletePlaylistAsync(id, User.UserId());

        await _eventBus.PublishAsync(new LibraryRefreshedEvent { QueryKey = ["music-playlists"] });

        return Ok(
            new StatusResponseDto<string>
            {
                Data = (
                    result > 0 ? "Playlist deleted successfully" : "Playlist not found"
                ).Localize(),
                Status = "ok",
            }
        );
    }

    [HttpPost]
    [Route("{id:guid}/cover")]
    [Consumes("multipart/form-data")]
    [Authorize(Policy = "Moderator")]
    public async Task<IActionResult> Cover(Guid id, IFormFile image)
    {
        Playlist? playlist = await _musicRepository.GetPlaylistForCoverAsync(id, User.UserId());

        if (playlist is null)
            return NotFoundResponse("Playlist not found");

        string slug = playlist.Name.ToSlug();

        await using Stream servedCopy = image.OpenReadStream();
        SavedMusicCover saved = await _coverStore.SaveAsync(slug, servedCopy);
        string cover = saved.Cover;
        string colorPalette = saved.ColorPalette;

        await _musicRepository.UpdatePlaylistCoverAsync(id, User.UserId(), cover, colorPalette);

        await _eventBus.PublishAsync(
            new LibraryRefreshedEvent { QueryKey = ["music", "playlists", playlist.Id] }
        );

        playlist._colorPalette = colorPalette;

        return Ok(
            new StatusResponseDto<ImageUploadResponseDto>
            {
                Status = "ok",
                Message = "Playlist cover updated",
                Data = new()
                {
                    Url = new($"/images/music/{slug}.jpg", UriKind.Relative),
                    ColorPalette = playlist.ColorPalette,
                },
            }
        );
    }

    [HttpPost]
    [Route("{id:guid}/tracks")]
    [Authorize(Policy = "MediaAccess")]
    public async Task<IActionResult> AddTrack(
        Guid id,
        [FromBody] CreatePlaylistTrackRequestDto request
    )
    {
        int result = await _musicRepository.AddPlaylistTrackAsync(id, request.Id, User.UserId());

        if (result < 0)
            return NotFoundResponse("Playlist not found");

        await _eventBus.PublishAsync(
            new LibraryRefreshedEvent { QueryKey = ["music", "playlists", id] }
        );

        return Ok(
            new StatusResponseDto<string>
            {
                Data = (
                    result > 0 ? "Playlist updated successfully" : "No changes made"
                ).Localize(),
                Status = "ok",
            }
        );
    }

    [HttpDelete]
    [Route("{id:guid}/tracks/{trackId:guid}")]
    [Authorize(Policy = "MediaAccess")]
    public async Task<IActionResult> RemoveTrack(Guid id, Guid trackId)
    {
        int result = await _musicRepository.RemovePlaylistTrackAsync(id, trackId, User.UserId());

        if (result < 0)
            return NotFoundResponse("Track not found in playlist");

        await _eventBus.PublishAsync(
            new LibraryRefreshedEvent { QueryKey = ["music", "playlists", id] }
        );

        return Ok(
            new StatusResponseDto<string>
            {
                Data = (
                    result > 0 ? "Playlist updated successfully" : "No changes made"
                ).Localize(),
                Status = "ok",
            }
        );
    }
}
