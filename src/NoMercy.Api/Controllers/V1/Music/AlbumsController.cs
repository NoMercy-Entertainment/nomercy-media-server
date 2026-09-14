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
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NoMercy.Api.DTOs.Common;
using NoMercy.Api.DTOs.Media;
using NoMercy.Api.DTOs.Media.Components;
using NoMercy.Api.DTOs.Music;
using NoMercy.Authorization;
using NoMercy.Data.Repositories;
using NoMercy.Database.Models.Music;
using NoMercy.Events;
using NoMercy.Events.Library;
using NoMercy.Events.Music;
using NoMercy.MediaProcessing.Images;
using NoMercy.MediaProcessing.Jobs;
using NoMercy.MediaProcessing.Jobs.PaletteJobs;
using NoMercy.NmSystem.Extensions;
using NoMercy.NmSystem.Information;
using NoMercy.Storage;
using NoMercyQueue;

namespace NoMercy.Api.Controllers.V1.Music;

[ApiController]
[Tags("Music Albums")]
[Authorize(Policy = "MediaAccess")]
[Route("api/v{version:apiVersion}/music/albums")]
public class AlbumsController : BaseController
{
    private readonly IMusicRepository _musicRepository;
    private readonly IEventBus _eventBus;
    private readonly IJobDispatcher _jobDispatcher;
    private readonly IMusicCoverStore _coverStore;

    private readonly ILogger<AlbumsController> _logger;

    public AlbumsController(
        ILogger<AlbumsController> logger,
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
    [Route("/api/v{version:apiVersion}/music/albums/letter/{letter}")]
    public async Task<IActionResult> Index(string letter, [FromQuery] PageRequestDto request)
    {
        Guid userId = User.UserId();
        if (!AuthPolicy.IsAllowed(User))
            return UnauthorizedResponse("You do not have permission to view albums");

        string language = Language();

        // Lolomo with the "all" marker (`_`) returns one carousel per first-letter
        // bucket in alphabetical order, with the symbol bucket (#) at the end.
        if (request.Version == "lolomo" && (letter == "_" || letter == "all"))
        {
            List<AlbumCardDto> allCards = await _musicRepository.GetAllAlbumCardsAsync(
                userId,
                language
            );

            List<ComponentEnvelope> items = [Component.Container()];

            IOrderedEnumerable<IGrouping<string, AlbumCardDto>> groups = allCards
                .GroupBy(a => AlphaBucket.LetterFor(a.Name))
                .OrderBy(g => g.Key == "#" ? "zz" : g.Key);

            foreach (IGrouping<string, AlbumCardDto> group in groups)
            {
                items.Add(
                    Component
                        .Carousel()
                        .WithId($"albums-{group.Key.ToLowerInvariant()}")
                        .WithTitle($"Albums: {group.Key}".Localize())
                        .WithItems(group.Select(a => Component.MusicCard(new MusicCardData(a))))
                );
            }

            return Ok(ComponentResponse.From(items));
        }

        List<AlbumCardDto> albumCards = await _musicRepository.GetAlbumCardsAsync(
            userId,
            letter,
            language
        );

        string displayLetter = letter == "_" ? "#" : letter.ToUpperInvariant();

        if (request.Version == "lolomo")
        {
            List<ComponentEnvelope> items =
            [
                Component.Container(),
                Component
                    .Carousel()
                    .WithId($"albums-{letter}")
                    .WithTitle($"Albums: {displayLetter}".Localize())
                    .WithItems(albumCards.Select(a => Component.MusicCard(new MusicCardData(a)))),
            ];

            return Ok(ComponentResponse.From(items));
        }

        ComponentEnvelope grid = Component
            .Grid()
            .WithId($"albums-{letter}")
            .WithTitle($"Albums: {displayLetter}".Localize())
            .WithItems(albumCards.Select(a => Component.MusicCard(new MusicCardData(a))));

        return Ok(ComponentResponse.From(grid));
    }

    [HttpGet]
    [Route("{id:guid}")]
    public async Task<IActionResult> Show(Guid id)
    {
        Guid userId = User.UserId();
        if (!AuthPolicy.IsAllowed(User))
            return UnauthorizedResponse("You do not have permission to view albums");

        string language = Language();

        Album? album = await _musicRepository.GetAlbumAsync(userId, id);

        if (album is null)
            return NotFoundResponse("Albums not found");

        if (string.IsNullOrEmpty(album._colorPalette) || album._colorPalette == "{}")
            _jobDispatcher.QueueColorPaletteInBackground("album", album.Id.ToString());

        return Ok(new AlbumResponseDto { Data = new(album, language) });
    }

    [HttpPost]
    [Route("{id:guid}/like")]
    public async Task<IActionResult> Like(Guid id, [FromBody] LikeRequestDto request)
    {
        Guid userId = User.UserId();
        if (!AuthPolicy.IsAllowed(User))
            return UnauthorizedResponse("You do not have permission to like albums");

        Album? album = await _musicRepository.GetAlbumAsync(userId, id);

        if (album is null)
            return UnprocessableEntityResponse("Albums not found");

        await _musicRepository.LikeAlbumAsync(userId, album, request.Value);

        await _eventBus.PublishAsync(
            new LibraryRefreshedEvent { QueryKey = ["music", "album", album.Id] }
        );

        await _eventBus.PublishAsync(
            new MusicItemLikedEvent
            {
                UserId = User.UserId(),
                ItemId = album.Id,
                ItemType = "album",
                Liked = request.Value,
            }
        );

        return Ok(
            new StatusResponseDto<string>
            {
                Status = "ok",
                Message = "{0} {1}",
                Args = new object[] { album.Name, request.Value ? "liked" : "unliked" },
            }
        );
    }

    [HttpPost]
    [Route("{id:guid}/rescan")]
    [Authorize(Policy = "Moderator")]
    public IActionResult Rescan(Guid id)
    {
        return Ok(
            new StatusResponseDto<string>
            {
                Status = "ok",
                Message = "Rescan started",
                Args = [],
            }
        );
    }

    [HttpPatch]
    [Route("{id:guid}")]
    [Authorize(Policy = "Moderator")]
    public async Task<IActionResult> Edit(Guid id, [FromBody] CreatePlaylistRequestDto request)
    {
        Album? album = await _musicRepository.GetAlbumForEditAsync(id);

        if (album is null)
            return NotFoundResponse("Album not found");

        string slug = album.Name.ToSlug();
        string colorPalette = album._colorPalette.OrEmpty();
        string cover = album.Cover.OrEmpty();

        if (request.Cover is not null)
        {
            byte[]? binData = ImageDataUri.Decode(request.Cover, out string? coverError);
            if (binData is null)
                return BadRequestResponse(coverError!);

            SavedMusicCover saved = await _coverStore.SaveAsync(slug, new MemoryStream(binData));
            cover = saved.Cover;
            colorPalette = saved.ColorPalette;
        }

        int result = await _musicRepository.UpdateAlbumMetadataAsync(
            id,
            request.Name,
            request.Description,
            cover,
            colorPalette
        );

        await _eventBus.PublishAsync(
            new LibraryRefreshedEvent { QueryKey = ["music", "album", id] }
        );

        return Ok(
            new StatusResponseDto<string>
            {
                Data = (result > 0 ? "Album updated successfully" : "No changes made").Localize(),
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
        Album? album = await _musicRepository.GetAlbumWithLibraryFolderAsync(id);

        if (album is null)
            return NotFoundResponse("Album not found");

        string slug = album.Name.ToSlug();

        await using (Stream libraryCopy = image.OpenReadStream())
            if (
                !await _coverStore.SaveToLibraryAsync(
                    album.LibraryFolder,
                    album.HostFolder,
                    "cover.jpg",
                    libraryCopy
                )
            )
                return UnprocessableEntityResponse("Album library folder not found");

        await using Stream servedCopy = image.OpenReadStream();
        SavedMusicCover saved = await _coverStore.SaveAsync(slug, servedCopy);
        string cover = saved.Cover;
        string colorPalette = saved.ColorPalette;

        await _musicRepository.UpdateAlbumCoverAsync(id, cover, colorPalette);

        album._colorPalette = colorPalette;

        return Ok(
            new StatusResponseDto<ImageUploadResponseDto>
            {
                Status = "ok",
                Message = "Album cover updated",
                Data = new()
                {
                    Url = new($"/images/music/{slug}.jpg", UriKind.Relative),
                    ColorPalette = album.ColorPalette,
                },
            }
        );
    }
}
