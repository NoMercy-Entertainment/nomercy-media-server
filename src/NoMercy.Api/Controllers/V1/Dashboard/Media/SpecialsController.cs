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

using System.ComponentModel.DataAnnotations.Schema;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using NoMercy.Api.DTOs.Common;
using NoMercy.Api.DTOs.Media;
using NoMercy.Authorization;
using NoMercy.Data.Repositories;
using NoMercy.Data.Requests;
using NoMercy.Data.Services;
using NoMercy.Database.Models.Movies;
using NoMercy.Database.Models.TvShows;
using NoMercy.NmSystem.Extensions;

namespace NoMercy.Api.Controllers.V1.Dashboard.Media;

[ApiController]
[Tags(tags: "Dashboard Specials")]
[ApiVersion(1.0)]
[Authorize(Policy = "Moderator")]
[Route("api/v{version:apiVersion}/dashboard/specials", Order = 11)]
public class SpecialsController(
    ISpecialRepository specialRepository,
    ILibraryLogicFactory libraryLogicFactory
) : BaseController
{
    [HttpGet]
    public async Task<IActionResult> Index()
    {
        List<Special> specials = await specialRepository.GetAllSpecialsAdminAsync();

        return Ok(
            new SpecialsResponseDto
            {
                Data = specials.Select(special => new SpecialsResponseItemDto(special)),
            }
        );
    }

    [HttpPost]
    public async Task<IActionResult> Store()
    {
        Guid userId = User.UserId();

        try
        {
            Special special = await specialRepository.CreateSpecialAsync(userId);

            return Ok(
                new StatusResponseDto<Special>
                {
                    Status = "ok",
                    Data = special,
                    Message = "Successfully created a new special.",
                    Args = [],
                }
            );
        }
        catch (Exception)
        {
            return InternalServerErrorResponse("Something went wrong creating the special");
        }
    }

    [HttpGet]
    [Route("{id:ulid}")]
    public async Task<IActionResult> Show(Ulid id)
    {
        Special? special = await specialRepository.GetSpecialByIdAsync(id);

        if (special is null)
            return NotFoundResponse("Special not found");

        return Ok(
            new StatusResponseDto<Special>
            {
                Status = "ok",
                Data = special,
                Message = "Successfully retrieved {0} special.",
                Args = [special.Title.OrEmpty()],
            }
        );
    }

    [HttpPatch]
    [Route("{id:ulid}")]
    public async Task<IActionResult> Update(Ulid id, [FromBody] SpecialUpdateRequest request)
    {
        try
        {
            Special? special = await specialRepository.UpdateSpecialAsync(
                id,
                request.Title,
                request.Overview,
                request.Poster,
                request.Backdrop,
                request.Logo
            );

            if (special is null)
                return NotFoundResponse("Special not found");

            return Ok(
                new StatusResponseDto<string>
                {
                    Status = "ok",
                    Message = "Successfully updated {0} special.",
                    Args = [special.Title.OrEmpty()],
                }
            );
        }
        catch (Exception)
        {
            return InternalServerErrorResponse("Something went wrong updating the special");
        }
    }

    [HttpDelete]
    [Route("{id:ulid}")]
    public async Task<IActionResult> Delete(Ulid id)
    {
        try
        {
            Special? special = await specialRepository.DeleteSpecialAsync(id);

            if (special is null)
                return NotFoundResponse("Special not found");

            return Ok(
                new StatusResponseDto<string>
                {
                    Status = "ok",
                    Message = "Successfully deleted {0} special.",
                    Args = [special.Title.OrEmpty()],
                }
            );
        }
        catch (Exception)
        {
            return InternalServerErrorResponse("Something went wrong deleting the special");
        }
    }

    [HttpPatch]
    [Route("sort")]
    public async Task<IActionResult> Sort([FromBody] LibrarySortRequest request)
    {
        List<Special> specials = await specialRepository.GetAllSpecialsSortableAsync();

        if (specials.Count == 0)
            return NotFoundResponse("No specials exist");

        return Ok(
            new StatusResponseDto<string>
            {
                Status = "ok",
                Message = "Successfully sorted specials.",
                Args = [],
            }
        );
    }

    [HttpPost]
    [Route("rescan")]
    public async Task<IActionResult> RescanAll()
    {
        List<Special> specialsList = await specialRepository.GetAllSpecialsForRescanAsync();

        if (specialsList.Count == 0)
            return NotFoundResponse("No specials exist");

        List<string?> titles = [];

        return Ok(
            new StatusResponseDto<List<string?>>
            {
                Status = "ok",
                Data = titles,
                Message = "Rescanning all specials.",
            }
        );
    }

    [HttpPost]
    [Route("{id:ulid}/rescan")]
    public async Task<IActionResult> Rescan(Ulid id)
    {
        LibraryLogic specialLogic = libraryLogicFactory.Create(id);

        if (await specialLogic.Process())
            return Ok(
                new StatusResponseDto<List<dynamic>>
                {
                    Status = "ok",
                    Data = specialLogic.Titles,
                    Message = "Rescanning {0} special.",
                    Args = [specialLogic.Id],
                }
            );

        return NotFoundResponse("Special not found");
    }

    [HttpGet]
    [Route("{id:ulid}/items")]
    public async Task<IActionResult> GetItems(Ulid id)
    {
        List<SpecialItem> items = await specialRepository.GetSpecialItemsAdminAsync(id);

        List<SpecialItemResponseDto> result = items
            .Select(si =>
            {
                if (si.MovieId is not null && si.Movie is not null)
                    return new()
                    {
                        Id = si.Id.ToString(),
                        Order = si.Order,
                        MediaType = "movie",
                        MediaId = si.Movie.Id,
                        Title = si.Movie.Title,
                        Overview = si.Movie.Overview,
                        Still = null,
                        Poster = si.Movie.Poster,
                        Year = si.Movie.ReleaseDate?.Year,
                        ShowTitle = null,
                        SeasonNumber = null,
                        EpisodeNumber = null,
                        Available = si.Movie.VideoFiles.Count > 0,
                    };

                if (si.EpisodeId is not null && si.Episode is not null)
                    return new SpecialItemResponseDto
                    {
                        Id = si.Id.ToString(),
                        Order = si.Order,
                        MediaType = "episode",
                        MediaId = si.Episode.Id,
                        Title = si.Episode.Title.OrEmpty(),
                        Overview = si.Episode.Overview,
                        Still = si.Episode.Still,
                        Poster = null,
                        Year = si.Episode.AirDate?.Year,
                        ShowTitle = si.Episode.Tv.Title,
                        SeasonNumber = si.Episode.SeasonNumber,
                        EpisodeNumber = si.Episode.EpisodeNumber,
                        Available = si.Episode.VideoFiles.Count > 0,
                    };

                return null;
            })
            .Where(x => x is not null)
            .ToList()!;

        return Ok(result);
    }

    [HttpPatch]
    [Route("{id:ulid}/items")]
    public async Task<IActionResult> UpdateItems(
        Ulid id,
        [FromBody] SpecialItemsUpdateRequest request
    )
    {
        List<SpecialItemReplacement> replacements = request
            .Items.Select(item => new SpecialItemReplacement(
                item.MediaType,
                item.MediaId,
                item.Order
            ))
            .ToList();

        bool found = await specialRepository.ReplaceSpecialItemsAsync(id, replacements);

        if (!found)
            return NotFoundResponse($"Special {id} does not exist.");

        return Ok(
            new StatusResponseDto<string>
            {
                Status = "ok",
                Message = "Successfully updated special items.",
            }
        );
    }

    [HttpGet]
    [Route("search")]
    public async Task<IActionResult> Search([FromQuery] string q, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(q) || q.Length < 2)
            return Ok(Array.Empty<SpecialSearchResultDto>());

        Task<List<Movie>> moviesTask = specialRepository.SearchMoviesAsync(q, take: 25, ct);
        Task<List<Episode>> episodesTask = specialRepository.SearchEpisodesAsync(q, take: 25, ct);

        await Task.WhenAll(moviesTask, episodesTask);

        List<SpecialSearchResultDto> results =
        [
            .. moviesTask.Result.Select(m => new SpecialSearchResultDto
            {
                Id = m.Id,
                MediaType = "movie",
                Title = m.Title,
                Overview = m.Overview,
                Still = null,
                Poster = m.Poster,
                Year = m.ReleaseDate?.Year,
                ShowTitle = null,
                SeasonNumber = null,
                EpisodeNumber = null,
                Available = m.VideoFiles.Count > 0,
            }),
            .. episodesTask.Result.Select(e => new SpecialSearchResultDto
            {
                Id = e.Id,
                MediaType = "episode",
                Title = e.Title.OrEmpty(),
                Overview = e.Overview,
                Still = e.Still,
                Poster = null,
                Year = e.AirDate?.Year,
                ShowTitle = e.Tv.Title,
                SeasonNumber = e.SeasonNumber,
                EpisodeNumber = e.EpisodeNumber,
                Available = e.VideoFiles.Count > 0,
            }),
        ];

        return Ok(results);
    }

    [NotMapped]
    public class SpecialUpdateRequest
    {
        [JsonProperty("id")]
        public Ulid Id { get; set; }

        [JsonProperty("title")]
        public string? Title { get; set; }

        [JsonProperty("overview")]
        public string? Overview { get; set; }

        [JsonProperty("poster")]
        public string? Poster { get; set; }

        [JsonProperty("backdrop")]
        public string? Backdrop { get; set; }

        [JsonProperty("logo")]
        public string? Logo { get; set; }
    }

    [NotMapped]
    public class SpecialItemResponseDto
    {
        [JsonProperty("id")]
        public string Id { get; set; } = string.Empty;

        [JsonProperty("order")]
        public int Order { get; set; }

        [JsonProperty("media_type")]
        public string MediaType { get; set; } = string.Empty;

        [JsonProperty("media_id")]
        public int MediaId { get; set; }

        [JsonProperty("title")]
        public string Title { get; set; } = string.Empty;

        [JsonProperty("overview")]
        public string? Overview { get; set; }

        [JsonProperty("still")]
        public string? Still { get; set; }

        [JsonProperty("poster")]
        public string? Poster { get; set; }

        [JsonProperty("year")]
        public int? Year { get; set; }

        [JsonProperty("show_title")]
        public string? ShowTitle { get; set; }

        [JsonProperty("season_number")]
        public int? SeasonNumber { get; set; }

        [JsonProperty("episode_number")]
        public int? EpisodeNumber { get; set; }

        [JsonProperty("available")]
        public bool Available { get; set; }
    }

    [NotMapped]
    public class SpecialSearchResultDto
    {
        [JsonProperty("id")]
        public int Id { get; set; }

        [JsonProperty("media_type")]
        public string MediaType { get; set; } = string.Empty;

        [JsonProperty("title")]
        public string Title { get; set; } = string.Empty;

        [JsonProperty("overview")]
        public string? Overview { get; set; }

        [JsonProperty("still")]
        public string? Still { get; set; }

        [JsonProperty("poster")]
        public string? Poster { get; set; }

        [JsonProperty("year")]
        public int? Year { get; set; }

        [JsonProperty("show_title")]
        public string? ShowTitle { get; set; }

        [JsonProperty("season_number")]
        public int? SeasonNumber { get; set; }

        [JsonProperty("episode_number")]
        public int? EpisodeNumber { get; set; }

        [JsonProperty("available")]
        public bool Available { get; set; }
    }

    [NotMapped]
    public class SpecialItemsUpdateRequest
    {
        [JsonProperty("items")]
        public List<SpecialItemUpdateDto> Items { get; set; } = [];
    }

    [NotMapped]
    public class SpecialItemUpdateDto
    {
        [JsonProperty("media_type")]
        public string MediaType { get; set; } = string.Empty;

        [JsonProperty("media_id")]
        public int MediaId { get; set; }

        [JsonProperty("order")]
        public int Order { get; set; }
    }
}
