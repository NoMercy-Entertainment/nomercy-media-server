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
using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.Api.Controllers.V1.Plugins;

/// <summary>
/// Every place the server can write, as the owner sees them.
/// <para>
/// What a picker offers, and the only thing it is allowed to send back. The
/// person choosing is at a browser, a phone or a television and the folder has
/// to exist on the server, so a path typed on the wrong machine is the bug an
/// id the host minted replaces.
/// </para>
/// </summary>
[ApiController]
[Tags("Plugins")]
[ApiVersion(1.0)]
[Authorize]
public class PluginLocationController(IPluginFolderCatalog catalog) : BaseController
{
    [HttpGet("api/v{version:apiVersion}/plugins/locations")]
    public async Task<IActionResult> Locations(CancellationToken ct)
    {
        IReadOnlyList<PluginStorageLocation> locations = await catalog.LocationsAsync(ct);

        return Ok(
            new DataResponseDto<IEnumerable<PluginLocationDto>>
            {
                Data = locations.Select(location => new PluginLocationDto
                {
                    Id = location.Id,
                    Name = location.Name,
                    Kind = location.Kind,
                    Writable = location.Writable,
                }),
            }
        );
    }

    /// <summary>
    /// What is in one of those places, so a file can be picked inside it.
    /// <para>
    /// Every entry's path is relative to the scope, which is what makes it safe
    /// to hand back: a caller cannot walk out of the folder the owner chose by
    /// sending one of these in again.
    /// </para>
    /// </summary>
    [HttpGet("api/v{version:apiVersion}/plugins/locations/{locationId}/files")]
    public async Task<IActionResult> Files(
        string locationId,
        [FromQuery] string? path,
        CancellationToken ct
    )
    {
        IPluginStorageScope? scope = await catalog.OpenAsync(locationId, ct);

        if (scope is null)
            return NotFoundResponse("No such location");

        List<PluginLocationEntryDto> entries = [];

        await foreach (PluginStorageEntry entry in scope.ListAsync(path ?? string.Empty, ct: ct))
        {
            entries.Add(
                new PluginLocationEntryDto
                {
                    Id = entry.Path,
                    Name = entry.Path.Split('/').Last(),
                    IsDirectory = entry.IsDirectory,
                    SizeBytes = entry.SizeBytes,
                }
            );
        }

        return Ok(new DataResponseDto<IEnumerable<PluginLocationEntryDto>> { Data = entries });
    }
}
