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
    private readonly IStorageFactory _storageFactory;

    private readonly ILogger<AlbumsController> _logger;

    public AlbumsController(
        ILogger<AlbumsController> logger,
        IMusicRepository musicService,
        IEventBus eventBus,
        IStorageFactory storageFactory
    )
    {
        _logger = logger;
        _musicRepository = musicService;
        _eventBus = eventBus;
        _storageFactory = storageFactory;
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

        // Fire-and-forget: enqueue takes the queue's global write lock (held by the
        // encoder workers), so dispatching inline blocked this read for seconds.
        if (string.IsNullOrEmpty(album._colorPalette) || album._colorPalette == "{}")
            _ = Task.Run(() =>
                QueueRunner.Current?.Dispatcher.Dispatch(
                    new ColorPaletteJob("album", album.Id.ToString()),
                    "palette",
                    1
                )
            );

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
            Match coverMatch = Regex.Match(request.Cover, "data:image/(?<type>.+?),(?<data>.+)");
            if (!coverMatch.Success)
                return BadRequestResponse("Cover must be a data:image/...;base64,... payload");

            byte[] binData;
            try
            {
                binData = Convert.FromBase64String(coverMatch.Groups["data"].Value);
            }
            catch (FormatException)
            {
                return BadRequestResponse("Cover payload is not valid base64");
            }

            cover = $"/{slug}.jpg";
            string filePath = Path.Combine(AppFiles.ImagesPath, "music", slug + ".jpg");

            await using (FileStream stream = new(filePath, FileMode.Create))
                await stream.WriteAsync(binData);

            colorPalette = await CoverArtImageManagerManager.ColorPalette("cover", new(filePath));
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

        IStorage folderStorage = _storageFactory.For(
            album.LibraryFolder.Id,
            album.LibraryFolder.DriverId,
            string.Empty
        );
        // Resolve through the driver, not the IStorage facade: the facade's
        // GetFullPath is a LocalStorage-only escape hatch that throws on every
        // remote backend, so a facade call here 500'd cover uploads for
        // NFS / SMB / S3 / WebDAV libraries.
        string libraryRootFolder = folderStorage.Driver.GetFullPath(album.LibraryFolder.Path);
        if (string.IsNullOrEmpty(libraryRootFolder))
            return UnprocessableEntityResponse("Album library folder not found");

        // save to album folder
        string filePath = Path.Combine(
            libraryRootFolder,
            album.HostFolder.TrimStart('\\'),
            "cover.jpg"
        );
        _logger.LogInformation(filePath);
        await using (FileStream stream = new(filePath, FileMode.Create))
        {
            await image.CopyToAsync(stream);
        }

        // save to app images folder
        string filePath2 = Path.Combine(AppFiles.ImagesPath, "music", slug + ".jpg");
        _logger.LogInformation(filePath2);
        await using (FileStream stream = new(filePath2, FileMode.Create))
        {
            await image.CopyToAsync(stream);
        }

        string cover = $"/{slug}.jpg";
        string colorPalette = await CoverArtImageManagerManager.ColorPalette(
            "cover",
            new(filePath2)
        );

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
