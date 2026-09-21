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
using NoMercy.Api.DTOs.Plugins;
using NoMercy.Authorization;
using NoMercy.Plugins.Abstractions;
using NoMercy.Plugins.Search;

namespace NoMercy.Api.Controllers.V1.Plugins;

/// <summary>
/// What the plugins found, beside what the library found.
///
/// <para>
/// Its own endpoint rather than folded into <c>/search</c>: the library answers
/// from a database in milliseconds and a plugin answers over somebody else's
/// network. One response for both made the fast half wait for the slow half.
/// </para>
/// </summary>
[ApiController]
[Tags("Plugins")]
[ApiVersion(1.0)]
[Authorize]
public class PluginSearchController(IPluginSearchService search) : BaseController
{
    /// <summary>How long the whole fan-out gets before the answer is sent without the stragglers.</summary>
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(3);

    [HttpGet("api/v{version:apiVersion}/plugins/search")]
    public async Task<IActionResult> Search(
        [FromQuery] string? query,
        [FromQuery] string? surface,
        CancellationToken ct
    )
    {
        string asking = PluginSurface.IsKnown(surface) ? surface! : PluginSurface.Web;

        PluginCaller caller = new(
            new UserId(new Ulid(User.UserId())),
            User.UserName(),
            User.Role() == "owner" ? PluginRole.Owner : PluginRole.Member,
            PluginAccess.Owned,
            Request.Headers.AcceptLanguage.ToString() is { Length: > 0 } locale ? locale : "en",
            asking
        );

        IReadOnlyList<PluginSearchGroup> groups = await search.SearchAsync(
            query ?? string.Empty,
            caller,
            Deadline,
            ct
        );

        return Ok(
            new DataResponseDto<IEnumerable<PluginSearchGroupDto>>
            {
                Data = groups.Select(group => new PluginSearchGroupDto
                {
                    PluginId = group.PluginId,
                    PluginName = group.PluginName,
                    Results = group
                        .Results.Select(result => new PluginSearchResultDto
                        {
                            Id = result.Id,
                            Title = result.Title,
                            Subtitle = result.Subtitle,
                            Cover = result.Cover?.ToString(),
                            Route = result.Route,
                            Params = result.Params,
                        })
                        .ToList(),
                }),
            }
        );
    }
}
