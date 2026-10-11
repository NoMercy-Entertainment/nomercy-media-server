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

using System.Net;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NoMercy.Api.DTOs.Common;
using NoMercy.Api.DTOs.Security;
using NoMercy.Api.Security;
using NoMercy.Database.Models.Security;
using NoMercy.Networking.Http;

namespace NoMercy.Api.Controllers.V1.Dashboard.Admin;

/// <summary>
/// The bans a server owner can see, add and lift, plus the thresholds that
/// produce them. A ban nobody can see or undo is a support ticket waiting to
/// happen, so this ships with the enforcement rather than after it.
/// </summary>
[ApiController]
[Tags("Dashboard Security")]
[ApiVersion(1.0)]
[Authorize]
[Route("api/v{version:apiVersion}/dashboard/security", Order = 10)]
public class SecurityController(
    IAbuseGuard abuseGuard,
    IAbuseGuardSettings abuseGuardSettings,
    IBlocklistFeedSettings blocklistFeedSettings
) : BaseController
{
    [HttpGet]
    [Route("bans")]
    [Authorize(Policy = "Moderator")]
    public async Task<IActionResult> Bans(CancellationToken ct)
    {
        List<IpBan> bans = await abuseGuard.ActiveBansAsync(ct);

        return Ok(
            new StatusResponseDto<List<IpBanDto>>
            {
                Status = "ok",
                Data = [.. bans.Select(IpBanDto.From)],
            }
        );
    }

    [HttpPost]
    [Route("bans")]
    [Authorize(Policy = "Moderator")]
    public async Task<IActionResult> Ban([FromBody] BanRequest request, CancellationToken ct)
    {
        if (!IPAddress.TryParse(request.Address, out IPAddress? address))
            return BadRequestResponse("Not a valid IP address.");

        if (ClientIpResolver.IsPrivateNetwork(address))
            return BadRequestResponse("Refusing to ban an address on your own network.");

        int minutes = request.Minutes > 0 ? request.Minutes : 60;

        IpBan ban = await abuseGuard.BanAsync(
            address,
            request.Reason ?? "Manual",
            TimeSpan.FromMinutes(minutes),
            manual: true,
            ct
        );

        return Ok(new StatusResponseDto<IpBanDto> { Status = "ok", Data = IpBanDto.From(ban) });
    }

    [HttpDelete]
    [Route("bans/{address}")]
    [Authorize(Policy = "Moderator")]
    public async Task<IActionResult> Unban(string address, CancellationToken ct)
    {
        bool removed = await abuseGuard.UnbanAsync(address, ct);
        if (!removed)
            return NotFoundResponse("No ban found for that address.");

        return Ok(new StatusResponseDto<object> { Status = "ok", Data = new { address } });
    }

    [HttpGet]
    [Route("blocklist-url")]
    [Authorize(Policy = "Moderator")]
    public async Task<IActionResult> BlocklistUrl(CancellationToken ct)
    {
        string token = await blocklistFeedSettings.EnsureTokenAsync(ct);

        return Ok(
            new StatusResponseDto<object> { Status = "ok", Data = new { url = FeedUrl(token) } }
        );
    }

    [HttpPost]
    [Route("blocklist-url/rotate")]
    [Authorize(Policy = "Moderator")]
    public async Task<IActionResult> RotateBlocklistUrl(CancellationToken ct)
    {
        string token = await blocklistFeedSettings.RotateTokenAsync(ct);

        return Ok(
            new StatusResponseDto<object> { Status = "ok", Data = new { url = FeedUrl(token) } }
        );
    }

    [HttpGet]
    [Route("settings")]
    [Authorize(Policy = "Moderator")]
    public IActionResult Settings()
    {
        return Ok(
            new StatusResponseDto<AbuseGuardSettingsDto>
            {
                Status = "ok",
                Data = new()
                {
                    Enabled = abuseGuardSettings.Enabled,
                    MaxScore = abuseGuardSettings.MaxScore,
                    WindowMinutes = (int)abuseGuardSettings.Window.TotalMinutes,
                    BanMinutes = (int)abuseGuardSettings.BanDuration.TotalMinutes,
                    MaxBanMinutes = (int)abuseGuardSettings.MaxBanDuration.TotalMinutes,
                    Allowlist = abuseGuardSettings.AllowlistText,
                },
            }
        );
    }

    [HttpPatch]
    [Route("settings")]
    [Authorize(Policy = "Moderator")]
    public async Task<IActionResult> UpdateSettings(
        [FromBody] AbuseGuardSettingsRequest request,
        CancellationToken ct
    )
    {
        if (request.MaxScore is < 1)
            return BadRequestResponse("The offence threshold must be at least 1.");

        if (request.WindowMinutes is < 1)
            return BadRequestResponse("The window must be at least 1 minute.");

        if (request.BanMinutes is < 1)
            return BadRequestResponse("A ban must last at least 1 minute.");

        if (request.MaxBanMinutes is < 1)
            return BadRequestResponse("The longest ban must be at least 1 minute.");

        if (request.Allowlist is not null)
        {
            foreach (
                string entry in request.Allowlist.Split(',', StringSplitOptions.RemoveEmptyEntries)
            )
                if (!IpRange.TryParse(entry, out IpRange _))
                    return BadRequestResponse($"Not a valid address or CIDR range: {entry.Trim()}");
        }

        if (request.Enabled is not null)
            await abuseGuardSettings.SetAsync(
                AbuseGuardSettings.EnabledKey,
                request.Enabled.Value.ToString(),
                ct
            );

        if (request.MaxScore is not null)
            await abuseGuardSettings.SetAsync(
                AbuseGuardSettings.MaxScoreKey,
                request.MaxScore.Value.ToString(),
                ct
            );

        if (request.WindowMinutes is not null)
            await abuseGuardSettings.SetAsync(
                AbuseGuardSettings.WindowMinutesKey,
                request.WindowMinutes.Value.ToString(),
                ct
            );

        if (request.BanMinutes is not null)
            await abuseGuardSettings.SetAsync(
                AbuseGuardSettings.BanMinutesKey,
                request.BanMinutes.Value.ToString(),
                ct
            );

        if (request.MaxBanMinutes is not null)
            await abuseGuardSettings.SetAsync(
                AbuseGuardSettings.MaxBanMinutesKey,
                request.MaxBanMinutes.Value.ToString(),
                ct
            );

        if (request.Allowlist is not null)
            await abuseGuardSettings.SetAsync(
                AbuseGuardSettings.AllowlistKey,
                request.Allowlist,
                ct
            );

        return Settings();
    }

    private string FeedUrl(string token) =>
        $"{Request.Scheme}://{Request.Host}/security/blocklist/{token}";
}
