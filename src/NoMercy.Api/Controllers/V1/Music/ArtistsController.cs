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
[ApiVersion(1.0)]
[Tags("Music Artists")]
[Authorize(Policy = "MediaAccess")]
[Route("api/v{version:apiVersion}/music/artists")]
public class ArtistsController : BaseController
{
    private readonly IMusicRepository _musicRepository;
    private readonly IEventBus _eventBus;
    private readonly IStorageFactory _storageFactory;

    private readonly ILogger<ArtistsController> _logger;

    public ArtistsController(
        ILogger<ArtistsController> logger,
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
    [Route("/api/v{version:apiVersion}/music/artists/letter/{letter}")]
    public async Task<IActionResult> Index(string letter, [FromQuery] PageRequestDto request)
    {
        Guid userId = User.UserId();
        if (!AuthPolicy.IsAllowed(User))
            return UnauthorizedResponse("You do not have permission to view artists");

        // Lolomo with the "all" marker (`_`) returns one carousel per first-letter
        // bucket in alphabetical order, with the symbol bucket (#) at the end.
        if (request.Version == "lolomo" && (letter == "_" || letter == "all"))
        {
            List<ArtistCardDto> allCards = await _musicRepository.GetAllArtistCardsAsync(userId);

            List<ComponentEnvelope> items = [Component.Container()];

            IOrderedEnumerable<IGrouping<string, ArtistCardDto>> groups = allCards
                .GroupBy(a => AlphaBucket.LetterFor(a.Name))
                .OrderBy(g => g.Key == "#" ? "zz" : g.Key);

            foreach (IGrouping<string, ArtistCardDto> group in groups)
            {
                items.Add(
                    Component
                        .Carousel()
                        .WithId($"artists-{group.Key.ToLowerInvariant()}")
                        .WithTitle($"Artists: {group.Key}".Localize())
                        .WithItems(group.Select(a => Component.MusicCard(new MusicCardData(a))))
                );
            }

            return Ok(ComponentResponse.From(items));
        }

        List<ArtistCardDto> artistCards = await _musicRepository.GetArtistCardsAsync(
            userId,
            letter
        );

        string displayLetter = letter == "_" ? "#" : letter.ToUpperInvariant();

        if (request.Version == "lolomo")
        {
            List<ComponentEnvelope> items =
            [
                Component.Container(),
                Component
                    .Carousel()
                    .WithId($"artists-{letter}")
                    .WithTitle($"Artists: {displayLetter}".Localize())
                    .WithItems(artistCards.Select(a => Component.MusicCard(new MusicCardData(a)))),
            ];

            return Ok(ComponentResponse.From(items));
        }

        ComponentEnvelope grid = Component
            .Grid()
            .WithId($"artists-{letter}")
            .WithTitle($"Artists: {displayLetter}".Localize())
            .WithItems(artistCards.Select(a => Component.MusicCard(new MusicCardData(a))));

        return Ok(ComponentResponse.From(grid));
    }

    [HttpGet]
    [Route("{id:guid}")]
    public async Task<IActionResult> Show(Guid id)
    {
        Guid userId = User.UserId();
        if (!AuthPolicy.IsAllowed(User))
            return UnauthorizedResponse("You do not have permission to view artists");

        Artist? artist = await _musicRepository.GetArtistAsync(userId, id);

        string country = Country();

        if (artist is null)
            return NotFoundResponse("Artist not found");

        // Fire-and-forget: enqueue serializes on the queue's global write lock, which
        // the busy encoder workers hold while touching the large queue DB. Awaiting it
        // inline made this read block for seconds. The palette is a background enrichment.
        if (string.IsNullOrEmpty(artist._colorPalette) || artist._colorPalette == "{}")
            _ = Task.Run(() =>
                QueueRunner.Current?.Dispatcher.Dispatch(
                    new ColorPaletteJob("artist", artist.Id.ToString()),
                    "palette",
                    1
                )
            );

        return Ok(new ArtistResponseDto { Data = new(artist, userId, country) });
    }

    [HttpPost]
    [Route("{id:guid}/like")]
    public async Task<IActionResult> Like(Guid id, [FromBody] LikeRequestDto request)
    {
        Guid userId = User.UserId();
        if (!AuthPolicy.IsAllowed(User))
            return UnauthorizedResponse("You do not have permission to like artists");

        Artist? artist = await _musicRepository.GetArtistByIdAsync(id);

        if (artist is null)
            return UnprocessableEntityResponse("Artist not found");

        await _musicRepository.LikeArtistAsync(userId, artist, request.Value);

        await _eventBus.PublishAsync(
            new LibraryRefreshedEvent { QueryKey = ["music", "artist", artist.Id] }
        );

        await _eventBus.PublishAsync(
            new MusicItemLikedEvent
            {
                UserId = User.UserId(),
                ItemId = artist.Id,
                ItemType = "artist",
                Liked = request.Value,
            }
        );

        return Ok(
            new StatusResponseDto<string>
            {
                Status = "ok",
                Message = "{0} {1}",
                Args = new object[] { artist.Name, request.Value ? "liked" : "unliked" },
            }
        );
    }

    /// <summary>
    /// Nothing ever backfills an existing artist's FanArt images: they're only
    /// ever fetched once, at onboarding, and this stub — despite being the one
    /// endpoint named for exactly this — never actually did anything. An
    /// artist onboarded before <c>FanArtImageManager.Add</c> started persisting
    /// backgrounds/logos/banners (or one whose fetch failed then) stays without
    /// them forever otherwise.
    /// </summary>
    [HttpPost]
    [Route("{id:guid}/rescan")]
    [Authorize(Policy = "Moderator")]
    public async Task<IActionResult> Rescan(Guid id)
    {
        Artist? artist = await _musicRepository.GetArtistByIdAsync(id);
        if (artist is null)
            return NotFoundResponse("Artist not found");

        await FanArtImageManager.Add(id, true);

        await _eventBus.PublishAsync(
            new LibraryRefreshedEvent { QueryKey = ["music", "artist", id] }
        );

        return Ok(
            new StatusResponseDto<string>
            {
                Status = "ok",
                Message = "{0} images refreshed",
                Args = [artist.Name],
            }
        );
    }

    [HttpDelete]
    [Route("{id:guid}")]
    [Authorize(Policy = "Moderator")]
    public async Task<IActionResult> Destroy(Guid id)
    {
        bool deleted = await _musicRepository.DeleteArtistAsync(id);

        await _eventBus.PublishAsync(new LibraryRefreshedEvent { QueryKey = ["music", "artist"] });

        return Ok(
            new StatusResponseDto<string>
            {
                Data = (deleted ? "Artist deleted successfully" : "Artist not found").Localize(),
                Status = "ok",
            }
        );
    }

    [HttpPatch]
    [Route("{id:guid}")]
    [Authorize(Policy = "Moderator")]
    public async Task<IActionResult> Edit(Guid id, [FromBody] UpdateMusicMetadataRequestDto request)
    {
        Artist? artist = await _musicRepository.GetArtistForEditAsync(id);

        if (artist is null)
            return NotFoundResponse("Artist not found");

        string slug = artist.Name.ToSlug();
        string colorPalette = artist._colorPalette.OrEmpty();
        string cover = artist.Cover.OrEmpty();

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

        int result = await _musicRepository.UpdateArtistMetadataAsync(
            id,
            request.Name ?? artist.Name,
            request.Description,
            cover,
            colorPalette
        );

        await _eventBus.PublishAsync(
            new LibraryRefreshedEvent { QueryKey = ["music", "artist", id] }
        );

        return Ok(
            new StatusResponseDto<string>
            {
                Data = (result > 0 ? "Artist updated successfully" : "No changes made").Localize(),
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
        Artist? artist = await _musicRepository.GetArtistWithLibraryFolderAsync(id);

        if (artist is null)
            return NotFoundResponse("Artist not found");

        string slug = artist.Name.ToSlug();

        IStorage folderStorage = _storageFactory.For(
            artist.LibraryFolder.Id,
            artist.LibraryFolder.DriverId,
            string.Empty
        );
        // Resolve through the driver, not the IStorage facade: the facade's
        // GetFullPath is a LocalStorage-only escape hatch that throws on every
        // remote backend, so a facade call here 500'd cover uploads for
        // NFS / SMB / S3 / WebDAV libraries.
        string libraryRootFolder = folderStorage.Driver.GetFullPath(artist.LibraryFolder.Path);
        if (string.IsNullOrEmpty(libraryRootFolder))
            return UnprocessableEntityResponse("Artist library folder not found");

        // save to artist folder
        string filePath = Path.Combine(
            libraryRootFolder,
            artist.HostFolder.TrimStart('\\'),
            slug + ".jpg"
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

        await _musicRepository.UpdateArtistCoverAsync(id, cover, colorPalette);

        await _eventBus.PublishAsync(
            new LibraryRefreshedEvent { QueryKey = ["music", "artist", artist.Id] }
        );

        artist._colorPalette = colorPalette;

        return Ok(
            new StatusResponseDto<ImageUploadResponseDto>
            {
                Status = "ok",
                Message = "Artist cover updated",
                Data = new()
                {
                    Url = new($"/images/music/{slug}.jpg", UriKind.Relative),
                    ColorPalette = artist.ColorPalette,
                },
            }
        );
    }
}
