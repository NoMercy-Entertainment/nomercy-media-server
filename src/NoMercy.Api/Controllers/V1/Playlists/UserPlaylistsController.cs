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
using NoMercy.Api.DTOs.Common;
using NoMercy.Api.DTOs.Media;
using NoMercy.Api.DTOs.Playlists;
using NoMercy.Authorization;
using NoMercy.Data.Repositories;
using NoMercy.Database.Models.Playlists;
using NoMercy.NmSystem.Extensions;

namespace NoMercy.Api.Controllers.V1.Playlists;

/// <summary>
/// User-created, ordered, VIDEO-ONLY playlists (movies + tv shows + episodes +
/// specials in one list — never music tracks). Deliberately routed at the
/// top-level "api/v{version}/playlists" — NOT under "/music/" — so it never
/// collides with NoMercy.Api.Controllers.V1.Music.PlaylistsController, the
/// separate music-only Playlist/PlaylistTrack feature this controller never
/// touches. The two features share no table, so a music playlist can never
/// appear in this controller's responses and vice versa. Every action is
/// scoped to the caller's own playlists via IUserPlaylistRepository's
/// ownership-checked methods.
/// </summary>
[ApiController]
[ApiVersion(1.0)]
[Tags("Playlists")]
[Authorize]
[Route("api/v{version:apiVersion}/playlists")]
public class UserPlaylistsController(IUserPlaylistRepository userPlaylistRepository)
    : BaseController
{
    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct = default)
    {
        Guid userId = User.UserId();
        if (!AuthPolicy.IsAllowed(User))
            return UnauthorizedResponse("You do not have permission to view playlists");

        List<UserPlaylistSummary> playlists = await userPlaylistRepository.GetUserPlaylistsAsync(
            userId,
            ct
        );

        return Ok(
            new CarouselResponseDto<UserPlaylistSummaryDto>
            {
                Data = playlists.Select(p => new UserPlaylistSummaryDto(p)),
            }
        );
    }

    [HttpGet]
    [Route("{id:guid}")]
    public async Task<IActionResult> Show(Guid id, CancellationToken ct = default)
    {
        Guid userId = User.UserId();
        if (!AuthPolicy.IsAllowed(User))
            return UnauthorizedResponse("You do not have permission to view playlists");

        UserPlaylistDetail? playlist = await userPlaylistRepository.GetPlaylistAsync(
            id,
            userId,
            ct
        );
        if (playlist is null)
            return NotFoundResponse("Playlist not found");

        string language = Language();
        string country = Country();

        List<PlaylistItem>? items = await userPlaylistRepository.GetPlaylistItemsAsync(
            id,
            userId,
            language,
            country,
            ct
        );

        return Ok(
            new DataResponseDto<UserPlaylistDetailDto>
            {
                Data = new()
                {
                    Id = playlist.Id,
                    Name = playlist.Name,
                    Description = playlist.Description,
                    Cover = playlist.Cover,
                    Items = [.. (items ?? []).Select(PlaylistItemCardDto.From)],
                },
            }
        );
    }

    [HttpPost]
    [Authorize(Policy = "MediaAccess")]
    public async Task<IActionResult> Create(
        [FromBody] CreateUserPlaylistRequestDto request,
        CancellationToken ct = default
    )
    {
        Guid userId = User.UserId();

        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequestResponse("Name is required");

        Guid id = await userPlaylistRepository.CreatePlaylistAsync(
            userId,
            request.Name,
            request.Description,
            request.Cover,
            ct
        );

        return Ok(
            new StatusResponseDto<UserPlaylistSummaryDto>
            {
                Status = "ok",
                Data = new()
                {
                    Id = id,
                    Name = request.Name,
                    Cover = request.Cover,
                    ItemCount = 0,
                },
            }
        );
    }

    [HttpPatch]
    [Route("{id:guid}")]
    [Authorize(Policy = "MediaAccess")]
    public async Task<IActionResult> Edit(
        Guid id,
        [FromBody] UpdateUserPlaylistRequestDto request,
        CancellationToken ct = default
    )
    {
        Guid userId = User.UserId();

        if (request.Name is not null && string.IsNullOrWhiteSpace(request.Name))
            return BadRequestResponse("Name is required");

        bool updated = await userPlaylistRepository.UpdatePlaylistAsync(
            id,
            userId,
            request.Name,
            request.Description,
            request.Cover,
            ct
        );

        if (!updated)
            return NotFoundResponse("Playlist not found");

        return Ok(
            new StatusResponseDto<string> { Status = "ok", Data = "Playlist updated".Localize() }
        );
    }

    [HttpDelete]
    [Route("{id:guid}")]
    [Authorize(Policy = "MediaAccess")]
    public async Task<IActionResult> Destroy(Guid id, CancellationToken ct = default)
    {
        Guid userId = User.UserId();

        bool deleted = await userPlaylistRepository.DeletePlaylistAsync(id, userId, ct);

        if (!deleted)
            return NotFoundResponse("Playlist not found");

        return Ok(
            new StatusResponseDto<string> { Status = "ok", Data = "Playlist deleted".Localize() }
        );
    }

    [HttpPost]
    [Route("{id:guid}/items")]
    [Authorize(Policy = "MediaAccess")]
    public async Task<IActionResult> AddItem(
        Guid id,
        [FromBody] AddPlaylistItemRequestDto request,
        CancellationToken ct = default
    )
    {
        Guid userId = User.UserId();

        if (!await userPlaylistRepository.OwnsPlaylistAsync(id, userId, ct))
            return NotFoundResponse("Playlist not found");

        if (!PlaylistItemKindWire.TryParse(request.Kind, out PlaylistItemKind kind))
            return BadRequestResponse("Invalid kind. Expected one of: movie, tv, episode, special");

        PlaylistItemRef? itemRef = BuildItemRef(kind, request.MediaId);
        if (itemRef is null)
            return BadRequestResponse("Invalid media_id for the given kind");

        PlaylistItem? created = await userPlaylistRepository.AddItemAsync(
            id,
            userId,
            itemRef.Value,
            request.Order,
            ct
        );

        // Ownership was already confirmed above, so a null result here
        // unambiguously means the referenced media doesn't exist.
        if (created is null)
            return NotFoundResponse("Referenced media was not found");

        return Ok(
            new StatusResponseDto<string>
            {
                Status = "ok",
                Data = "Item added to playlist".Localize(),
            }
        );
    }

    [HttpDelete]
    [Route("{id:guid}/items/{itemId:ulid}")]
    [Authorize(Policy = "MediaAccess")]
    public async Task<IActionResult> RemoveItem(
        Guid id,
        Ulid itemId,
        CancellationToken ct = default
    )
    {
        Guid userId = User.UserId();

        bool removed = await userPlaylistRepository.RemoveItemAsync(id, userId, itemId, ct);

        if (!removed)
            return NotFoundResponse("Item not found in playlist");

        return Ok(
            new StatusResponseDto<string> { Status = "ok", Data = "Item removed".Localize() }
        );
    }

    [HttpPut]
    [Route("{id:guid}/items/order")]
    [Authorize(Policy = "MediaAccess")]
    public async Task<IActionResult> Reorder(
        Guid id,
        [FromBody] ReorderPlaylistItemsRequestDto request,
        CancellationToken ct = default
    )
    {
        Guid userId = User.UserId();

        if (!await userPlaylistRepository.OwnsPlaylistAsync(id, userId, ct))
            return NotFoundResponse("Playlist not found");

        List<Ulid> orderedIds = new(request.OrderedItemIds.Count);
        foreach (string rawId in request.OrderedItemIds)
        {
            if (!Ulid.TryParse(rawId, out Ulid parsed))
                return BadRequestResponse("Invalid item id in ordered_item_ids");
            orderedIds.Add(parsed);
        }

        bool reordered = await userPlaylistRepository.ReorderAsync(id, userId, orderedIds, ct);

        if (!reordered)
            return BadRequestResponse(
                "ordered_item_ids must match the playlist's current items exactly"
            );

        return Ok(
            new StatusResponseDto<string> { Status = "ok", Data = "Playlist reordered".Localize() }
        );
    }

    /// <summary>
    /// Parses <paramref name="mediaId"/> to the id type <paramref name="kind"/>
    /// expects (int for movie/tv/episode, Ulid for special). Returns null on a
    /// type mismatch so the caller can 400 instead of 500.
    /// </summary>
    private static PlaylistItemRef? BuildItemRef(PlaylistItemKind kind, string mediaId)
    {
        switch (kind)
        {
            case PlaylistItemKind.Movie:
                return int.TryParse(mediaId, out int movieId)
                    ? PlaylistItemRef.ForMovie(movieId)
                    : null;
            case PlaylistItemKind.Tv:
                return int.TryParse(mediaId, out int tvId) ? PlaylistItemRef.ForTv(tvId) : null;
            case PlaylistItemKind.Episode:
                return int.TryParse(mediaId, out int episodeId)
                    ? PlaylistItemRef.ForEpisode(episodeId)
                    : null;
            case PlaylistItemKind.Special:
                return Ulid.TryParse(mediaId, out Ulid specialId)
                    ? PlaylistItemRef.ForSpecial(specialId)
                    : null;
            default:
                return null;
        }
    }
}
