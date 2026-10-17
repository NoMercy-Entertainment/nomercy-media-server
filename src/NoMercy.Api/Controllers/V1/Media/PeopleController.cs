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
using NoMercy.Authorization;
using NoMercy.Data.Repositories;
using NoMercy.Database.Models.People;
using NoMercy.NmSystem.Information;
using NoMercy.Providers.TMDB.Client;
using NoMercy.Providers.TMDB.Models.People;

namespace NoMercy.Api.Controllers.V1.Media;

[ApiController]
[Tags(tags: "Media People")]
[ApiVersion(1.0)]
[Authorize(Policy = "MediaAccess")]
public class PeopleController(
    IPeopleRepository peopleRepository,
    IPersonMetadataProvider personMetadataProvider,
    IServerConfiguration config
) : BaseController
{
    [HttpGet]
    [Route("api/v{version:apiVersion}/person")] // match themoviedb.org API
    [ResponseCache(NoStore = true)]
    public async Task<IActionResult> Index([FromQuery] PageRequestDto request)
    {
        Guid userId = User.UserId();

        string language = Language();

        List<PeopleResponseItemDto> people = (
            await peopleRepository.GetPeopleAsync(userId, language, request.Take, request.Page)
        )
            .Select(person => new PeopleResponseItemDto(person))
            .ToList();

        return GetPaginatedResponse(people, request);
    }

    [HttpGet]
    [Route("/api/v{version:apiVersion}/person/{id:int}")] // match themoviedb.org API
    [ResponseCache(NoStore = true)]
    public async Task<IActionResult> Show(int id)
    {
        string country = Country();

        TmdbPersonAppends? personAppends;
        try
        {
            personAppends = await personMetadataProvider.GetPersonAsync(id, default);
        }
        catch (Exception)
        {
            // Mirror Movies/Tv: a transient provider (TMDB) failure degrades to a
            // clean 404 rather than surfacing an unhandled 500.
            return NotFoundResponse("Person not found");
        }

        if (personAppends is null)
            return NotFoundResponse("Person not found");

        if (personAppends.Adult && !config.ShowAdultContent)
            return UnauthorizedResponse(
                "Person is adult which is not allowed by the server configuration"
            );

        Person? person = await peopleRepository.GetPersonWithCreditsAsync(id);

        return Ok(new PersonResponseDto { Data = new(personAppends, country, person) });
    }
}
