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

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NoMercy.Api.Services.Music;
using NoMercy.Database.Models.Users;
using NoMercy.Networking;
using NoMercy.Networking.Cast;
using NoMercy.Networking.Discovery;
using NoMercy.NmSystem.Configuration;
using NoMercy.NmSystem.Information;
using NoMercy.Setup;
using NoMercy.Setup.Cast;

namespace NoMercy.Api.Services.Cast;

/// <summary>
/// Wakes a TV the server cannot reach over its own bus, from the server.
///
/// Casting used to be every client's own problem: the hubs answered
/// <c>cast_fallback</c> and each app drove a Cast SDK itself. That makes the
/// feature only as reliable as the weakest sender on the network, and every sender
/// is a different implementation of the same idea — an Android MediaRouter here, a
/// Chrome Cast SDK there, nothing at all on a platform that has neither. The server
/// sits on the same LAN, already holds the device's address and the token to mint,
/// and can do it once on behalf of all of them.
/// </summary>
public class ServerCastWaker(
    CastPanelWakeLauncher launcher,
    CastSessionTokenService tokens,
    IHttpContextAccessor httpContextAccessor,
    ILogger<ServerCastWaker> logger,
    INetworkDiscovery? networkDiscovery = null
) : IServerCastWaker
{
    /// <summary>
    /// Dispatches a Cast launch at <paramref name="tv" />.
    ///
    /// Returns false only when there is no address to dial. The launch is
    /// best-effort — whether the TV actually came up is answered by it reappearing
    /// on the bus, which is what the caller waits on, so a dispatched launch is
    /// reported the same way a bus wake is.
    /// </summary>
    public async Task<bool> WakeAsync(Device tv, Guid userId, CastIntent intent)
    {
        string? targetIp = CastAddress.Resolve(tv.LanIp, tv.Ip);
        if (targetIp is null)
        {
            logger.LogWarning(
                "[ServerCastWaker] no LAN address for '{Name}' — nothing to cast at",
                tv.Name
            );
            return false;
        }

        logger.LogInformation(
            "[ServerCastWaker] waking '{Name}' at {TargetIp} via Cast",
            [tv.Name, targetIp]
        );

        Ulid deviceId = tv.Id;
        string serverUrl = ResolveServerUrl();
        string locale = ResolveSenderLocale();

        // Android receiver, not the Web Receiver. The bus-registry check the music
        // panel wake uses as a proxy for "our app is installed" is exactly wrong
        // here: a TV asleep with its app closed is off the bus, and that is the one
        // case Cast Connect exists for. This is a registered TV of ours, so the
        // native app is what should come up.
        await launcher.LaunchIfColdAsync(
            targetIsLive: false,
            targetIp: targetIp,
            useAndroidReceiver: true,
            resolveLaunchData: () =>
                tokens.MintAsync(
                    userId: userId,
                    serverId: Info.DeviceId.ToString(),
                    serverUrl: serverUrl,
                    deviceId: deviceId,
                    intent: intent,
                    clientLocale: locale
                )
        );

        return true;
    }

    /// <summary>
    /// Public origin the receiver should use for API and SignalR, preferring the
    /// path Connectivity actually resolved over the configured base URL.
    /// </summary>
    private string ResolveServerUrl()
    {
        string? external = networkDiscovery?.ExternalAddress;
        return string.IsNullOrEmpty(external)
            ? ExternalServicesConfig.Current.ApiBaseUrl
            : external;
    }

    /// <summary>
    /// The caller's own locale, so the receiver seeds i18n correctly on first paint.
    /// </summary>
    private string ResolveSenderLocale()
    {
        string? header = httpContextAccessor.HttpContext?.Request.Headers.AcceptLanguage.ToString();
        if (string.IsNullOrEmpty(header))
            return "en-US";

        string first = header.Split(',')[0].Split(';')[0].Trim();
        return string.IsNullOrEmpty(first) ? "en-US" : first;
    }
}
