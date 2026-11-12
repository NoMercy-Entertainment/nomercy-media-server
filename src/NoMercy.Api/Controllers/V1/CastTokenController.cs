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
using Newtonsoft.Json;
using NoMercy.Api.Services.Cast;
using NoMercy.Authorization;
using NoMercy.Data.Repositories;
using NoMercy.Database.Models.Users;
using NoMercy.NmSystem.Information;

namespace NoMercy.Api.Controllers.V1;

/// <summary>
/// A short token scoped to one TV, so a phone stops sending its Keycloak token to a LAN
/// host. These literal routes win over the proxy's <c>{deviceId}/{**path}</c>.
/// </summary>
[ApiController]
[ApiVersion(1.0)]
[Authorize]
[Route("api/v{version:apiVersion}/cast")]
public class CastTokenController(
    IDeviceRepository deviceRepository,
    IUserCache userCache,
    CastControlTokenService tokens
) : BaseController
{
    // Public like /info: the TV holds no Keycloak token for this server's users.
    [AllowAnonymous]
    [HttpGet("keys")]
    public ContentResult Keys() => Content(tokens.PublicJwksJson(), "application/json");

    [HttpPost("{deviceId}/token")]
    public async Task<IActionResult> Mint(string deviceId)
    {
        Guid userId = User.UserId();
        if (userId == Guid.Empty)
            return UnauthenticatedResponse("No authenticated user on the cast token request");

        User? user = userCache.GetUser(userId);
        if (user is null || !(user.Allowed || user.Owner))
            return UnauthorizedResponse("This user may not use this server");

        Device? tv = await deviceRepository.GetByClientDeviceIdAsync(deviceId);
        if (tv is null)
            return NotFoundResponse($"No device '{deviceId}' found");

        if (tv.OwnerUserId != userId || tv.Type != "tv")
            return UnauthorizedResponse("This device is not in your device list");

        if (string.IsNullOrWhiteSpace(tv.Fingerprint))
            return StatusCode(
                StatusCodes.Status409Conflict,
                new { error = "device_fingerprint_unknown" }
            );

        CastControlToken minted = tokens.Mint(
            Info.DeviceId.ToString(),
            userId,
            tv.DeviceId,
            tv.Fingerprint
        );

        return Ok(
            new CastControlTokenResponse
            {
                Token = minted.Token,
                ExpiresAt = minted.ExpiresAt.UtcDateTime.ToString("O"),
                DeviceId = tv.DeviceId,
            }
        );
    }
}

public class CastControlTokenResponse
{
    [JsonProperty("token")]
    public string Token { get; set; } = string.Empty;

    [JsonProperty("expires_at")]
    public string ExpiresAt { get; set; } = string.Empty;

    [JsonProperty("device_id")]
    public string DeviceId { get; set; } = string.Empty;
}
