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

using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Lan;
using NoMercy.PluginSdk.Mvc;

namespace NoMercy.Tests.Api.Plugins;

/// <summary>
/// A plugin's controller that answers a LAN device by saying what it was
/// handed. Lives at namespace scope, public and unnested, because MVC only
/// discovers a controller the way it would find one inside a plugin assembly.
/// </summary>
public class LanSamplePluginController : PluginControllerBase
{
    [HttpGet("hdhr/discover.json")]
    public IActionResult Discover()
    {
        StringBuilder seen = new();

        seen.AppendLine($"device={LanCaller?.DeviceId}");
        seen.AppendLine($"name={LanCaller?.Name}");
        seen.AppendLine($"base={LanCaller?.BaseAddress}");
        seen.AppendLine($"path={Request.Path}");
        seen.AppendLine($"raw={HttpContext.Features.Get<IHttpRequestFeature>()?.RawTarget}");
        seen.AppendLine($"query={Request.QueryString}");
        seen.AppendLine($"authenticated={User.Identity?.IsAuthenticated == true}");
        seen.AppendLine($"claims={User.Claims.Count()}");
        seen.AppendLine(
            $"result={HttpContext.Features.Get<IAuthenticateResultFeature>()?.AuthenticateResult?.Principal?.Identity?.IsAuthenticated == true}"
        );
        seen.AppendLine($"authorization={Request.Headers.ContainsKey("Authorization")}");
        seen.AppendLine($"cookie={Request.Headers.ContainsKey("Cookie")}");
        seen.AppendLine(
            $"accessor={HttpContext.RequestServices.GetRequiredService<IPluginCallerAccessor>().CurrentUserId}"
        );
        seen.AppendLine($"caller={CallerOrRefusal()}");
        seen.AppendLine(
            $"minting-for={HttpContext.RequestServices.GetRequiredService<IPluginLanCallerAccessor>().Current?.DeviceId}"
        );

        return Content(seen.ToString());
    }

    [HttpGet("hdhr/auto/{channel}")]
    public IActionResult Tune(string channel) => Content($"channel={channel}");

    private static int _served;

    /// <summary>
    /// An answer the plugin marks as cacheable for ten minutes, numbered so a
    /// test can tell a fresh answer from one the server's cache kept.
    /// </summary>
    [HttpGet("hdhr/lineup_status.json")]
    public IActionResult Status()
    {
        Response.Headers.CacheControl = "public, max-age=600";

        return Content($"served={Interlocked.Increment(ref _served)}");
    }

    [HttpGet("hdhr/lineup.json")]
    public IActionResult Lineup() => Content("lineup");

    [HttpPost("hdhr/lineup.json")]
    public IActionResult ChangeLineup() => Content("changed");

    /// <summary>A real GET action the manifest does not name.</summary>
    [HttpGet("hdhr/secret.json")]
    public IActionResult Secret() => Content("secret");

    /// <summary>Holds the request open, the way a tuner reads a channel for hours.</summary>
    [HttpGet("hdhr/stream")]
    public async Task Stream()
    {
        Response.ContentType = "video/mp2t";
        await Response.Body.WriteAsync(new byte[] { 0x47 });
        await Response.Body.FlushAsync();

        try
        {
            await Task.Delay(Timeout.Infinite, HttpContext.RequestAborted);
        }
        catch (OperationCanceledException) { }
    }

    private string CallerOrRefusal()
    {
        try
        {
            return Caller.Id.ToString();
        }
        catch (PluginRefusedException)
        {
            return "refused";
        }
    }
}
