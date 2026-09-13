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
using NoMercy.Api.DTOs.Media;
using NoMercy.Api.DTOs.Media.Components;
using NoMercy.Api.DTOs.Music;
using NoMercy.Authorization;
using NoMercy.Data.Repositories;
using NoMercy.Database.Models.Music;
using NoMercy.NmSystem.Extensions;

namespace NoMercy.Api.Controllers.V1.Music;

[ApiController]
[ApiVersion(1.0)]
[Tags("Music Genres")]
[Authorize(Policy = "MediaAccess")]
[Route("api/v{version:apiVersion}/music/genres", Order = 4)]
public class GenresController : BaseController
{
    private readonly IGenreRepository _genreRepository;

    public GenresController(IGenreRepository genreRepository)
    {
        _genreRepository = genreRepository;
    }

    [HttpGet]
    public async Task<IActionResult> Index([FromQuery] PageRequestDto request)
    {
        Guid userId = User.UserId();

        List<MusicGenreCardDto> genreCards = await _genreRepository.GetMusicGenreCardsAsync(userId);
        IEnumerable<NmGenreCardDto> allGenres = genreCards
            .Select(genre => new NmGenreCardDto(genre))
            .DistinctBy(genre => genre.Title);

        bool isLolomo = string.Equals(
            request.Version,
            "lolomo",
            StringComparison.OrdinalIgnoreCase
        );

        if (isLolomo)
        {
            List<IGrouping<string, NmGenreCardDto>> groups =
            [
                .. allGenres
                    .GroupBy(g => AlphaBucket.LetterFor(g.Title))
                    .OrderBy(g => g.Key == "#" ? "zz" : g.Key),
            ];

            List<ComponentEnvelope> items = [Component.Container()];

            for (int i = 0; i < groups.Count; i++)
            {
                IGrouping<string, NmGenreCardDto> group = groups[i];
                string id = $"genres-{group.Key.ToLowerInvariant()}";
                string? prevId = i == 0 ? null : $"genres-{groups[i - 1].Key.ToLowerInvariant()}";
                string? nextId =
                    i == groups.Count - 1 ? null : $"genres-{groups[i + 1].Key.ToLowerInvariant()}";

                items.Add(
                    Component
                        .Carousel()
                        .WithId(id)
                        .WithNavigation(prevId, nextId)
                        .WithTitle($"Genres: {group.Key}".Localize())
                        .WithItems(group.Select(Component.GenreCard))
                );
            }

            return Ok(ComponentResponse.From(items));
        }

        ComponentEnvelope response = Component
            .Grid()
            .WithItems(allGenres.Select(Component.GenreCard));

        return Ok(ComponentResponse.From(response));
    }

    [HttpGet]
    [Route("letter/{letter}")]
    public async Task<IActionResult> LibraryByLetter(
        Ulid libraryId,
        string letter,
        [FromQuery] PageRequestDto request
    )
    {
        Guid userId = User.UserId();

        List<MusicGenreCardDto> genreCards =
            await _genreRepository.GetPaginatedMusicGenreCardsAsync(
                userId,
                letter,
                request.Take,
                request.Page
            );
        IEnumerable<NmGenreCardDto> genres = genreCards
            .Select(genre => new NmGenreCardDto(genre))
            .DistinctBy(genre => genre.Title);

        string displayLetter = letter == "_" ? "#" : letter.ToUpperInvariant();

        bool isLolomo = string.Equals(
            request.Version,
            "lolomo",
            StringComparison.OrdinalIgnoreCase
        );

        if (isLolomo)
        {
            List<ComponentEnvelope> items =
            [
                Component.Container(),
                Component
                    .Carousel()
                    .WithId($"genres-{letter}")
                    .WithTitle($"Genres: {displayLetter}".Localize())
                    .WithItems(genres.Select(Component.GenreCard)),
            ];

            return Ok(ComponentResponse.From(items));
        }

        ComponentEnvelope grid = Component
            .Grid()
            .WithId($"genres-{letter}")
            .WithTitle($"Genres: {displayLetter}".Localize())
            .WithItems(genres.Select(Component.GenreCard));

        return Ok(ComponentResponse.From(grid));
    }

    [HttpGet]
    [Route("{id:guid}")]
    public async Task<IActionResult> Show(Guid id)
    {
        Guid userId = User.UserId();

        string language = Language();

        MusicGenre? genre = await _genreRepository.GetMusicGenreAsync(userId, id);

        if (genre is null)
            return NotFoundResponse("Albums not found");

        return Ok(new GenreResponseDto { Data = new(genre, language) });
    }
}
