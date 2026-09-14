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
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NoMercy.Api.DTOs.Common;
using NoMercy.Api.DTOs.Media;
using NoMercy.Authorization;
using NoMercy.Data.Repositories;
using NoMercy.Database.Models.Users;
using NoMercy.Events;
using NoMercy.Events.Library;
using NoMercy.NmSystem.Domain;

namespace NoMercy.Api.Controllers.V1.Media;

[ApiController]
[ApiVersion(1.0)]
[Authorize]
[Route("api/v{version:apiVersion}/userData")]
public class UserDataController(
    IHomeRepository homeRepository,
    IUserDataRepository userDataRepository,
    IEventBus eventBus,
    ILogger<UserDataController> logger
) : BaseController
{
    [HttpGet]
    public IActionResult Index()
    {
        // Guid userId = User.UserId();
        if (!AuthPolicy.IsAllowed(User))
            return UnauthenticatedResponse("You do not have permission to view user data");

        return Ok(new PlaceholderResponse { Data = [] });
    }

    [HttpGet]
    [Route("continue")]
    [ResponseCache(NoStore = true)]
    public async Task<IActionResult> ContinueWatching()
    {
        Guid userId = User.UserId();
        if (!AuthPolicy.IsAllowed(User))
            return UnauthenticatedResponse("You do not have permission to view continue watching");

        string language = Language();
        string country = Country();

        HashSet<UserData> continueWatching = await homeRepository.GetContinueWatchingAsync(
            userId,
            language,
            country
        );

        return Ok(
            new CarouselResponseDto<NmCardDto>
            {
                Data = continueWatching
                    .Select(item => new NmCardDto(item, country))
                    .DistinctBy(item => item.Link),
            }
        );
    }

    [HttpDelete]
    [Route("continue")]
    public async Task<IActionResult> RemoveContinue(FavoriteRequest body)
    {
        Guid userId = User.UserId();
        if (!AuthPolicy.IsAllowed(User))
            return UnauthenticatedResponse(
                "You do not have permission to remove continue watching"
            );

        if (!TryParseFavoriteIds(body, out int? intId, out Ulid? ulidId))
            return BadRequestResponse("Invalid id for the requested type");

        List<UserData> userData = await userDataRepository.GetUserDataAsync(
            userId,
            body.Type,
            intId,
            ulidId
        );

        if (userData.Count == 0)
            return NotFoundResponse("Item not found");

        logger.LogInformation("{UserData}", userData);

        await userDataRepository.HideFromContinueWatchingAsync(userData);

        await eventBus.PublishAsync(new LibraryRefreshedEvent { QueryKey = ["continue-watching"] });

        return Ok(new StatusResponseDto<string> { Status = "ok", Message = "Item removed" });
    }

    [HttpGet]
    [Route("watched")]
    public async Task<IActionResult> Watched([FromQuery] FavoriteRequest body)
    {
        Guid userId = User.UserId();
        if (!AuthPolicy.IsAllowed(User))
            return UnauthenticatedResponse("You do not have permission to view watched");

        if (!TryParseFavoriteIds(body, out int? intId, out Ulid? ulidId))
            return BadRequestResponse("Invalid id for the requested type");

        UserData? userData = await userDataRepository.GetUserDataSingleAsync(
            userId,
            body.Type,
            intId,
            ulidId
        );

        if (userData == null)
            return NotFoundResponse("Item not found");

        return Ok(
            new StatusResponseDto<string> { Status = "ok", Message = "Item marked as watched" }
        );
    }

    [HttpGet]
    [Route("favorites")]
    [ResponseCache(NoStore = true)]
    public async Task<IActionResult> Favorites(CancellationToken ct = default)
    {
        Guid userId = User.UserId();
        if (!AuthPolicy.IsAllowed(User))
            return UnauthenticatedResponse("You do not have permission to view favorites");

        string language = Language();
        string country = Country();

        FavoritesData favorites = await homeRepository.GetFavoritesAsync(
            userId,
            language,
            country,
            ct
        );

        List<NmCardDto> cards =
        [
            .. favorites.Movies.Select(movie => new NmCardDto(movie, country)),
            .. favorites.TvShows.Select(tv => new NmCardDto(tv, country)),
            .. favorites.Collections.Select(collection => new NmCardDto(collection, country)),
            .. favorites.Specials.Select(special => new NmCardDto(special, country)),
        ];

        return Ok(
            new CarouselResponseDto<NmCardDto>
            {
                Data = cards
                    .OrderBy(card => card.Title, StringComparer.OrdinalIgnoreCase)
                    .DistinctBy(item => item.Link),
            }
        );
    }

    /// <summary>
    /// Parses <see cref="FavoriteRequest.Id"/> as either an int (movie / tv /
    /// collection) or a Ulid (special) depending on <see cref="FavoriteRequest.Type"/>.
    /// Returns false when the id is malformed for the requested type so the
    /// caller can short-circuit with a 400 instead of throwing FormatException
    /// from inside an EF query.
    /// </summary>
    private static bool TryParseFavoriteIds(FavoriteRequest body, out int? intId, out Ulid? ulidId)
    {
        intId = null;
        ulidId = null;
        if (string.IsNullOrEmpty(body.Id))
            return false;

        switch (body.Type)
        {
            case MediaTypes.MovieMediaType:
            case MediaTypes.TvMediaType:
            case MediaTypes.CollectionMediaType:
                if (!int.TryParse(body.Id, out int parsedInt))
                    return false;
                intId = parsedInt;
                return true;
            case MediaTypes.SpecialMediaType:
                if (!Ulid.TryParse(body.Id, out Ulid parsedUlid))
                    return false;
                ulidId = parsedUlid;
                return true;
            default:
                return false;
        }
    }
}
