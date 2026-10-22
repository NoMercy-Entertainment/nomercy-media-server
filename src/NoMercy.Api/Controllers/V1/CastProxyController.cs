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
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NoMercy.Authorization;
using NoMercy.Data.Repositories;
using NoMercy.Database.Models.Users;
using NoMercy.Networking.Http;

namespace NoMercy.Api.Controllers.V1;

/// <summary>
/// Reverse-proxy for a TV's own embedded <c>/v1</c> control server (Ktor, port
/// 7626). A browser served over HTTPS cannot reach <c>http://{lanIp}:7626</c>
/// directly (mixed-content + Private Network Access), so the web player casts
/// by talking to this endpoint over its existing HTTPS/JWT session; the server
/// forwards the request LAN-side to the TV and relays the reply back. This lets
/// the web reuse the exact <c>/v1</c> control surface the Android/KMP app uses
/// natively — <c>launch</c>, <c>session</c> polling, <c>session/transport/*</c>.
/// The caller's own bearer token is forwarded: the TV validates the same
/// Keycloak user token the phone presents and auto-pairs the owner by subject.
/// </summary>
[ApiController]
[ApiVersion(1.0)]
[Authorize]
[Route("api/v{version:apiVersion}/cast")]
public class CastProxyController(
    IDeviceRepository deviceRepository,
    IHttpClientFactory httpClientFactory,
    ILogger<CastProxyController> logger
) : BaseController
{
    private const int TvControlPort = 7626;

    /// <summary>
    /// The LAN address to forward to, and the port it answers on.
    ///
    /// <see cref="Device.Ip"/> is where the device was last SEEN from, which through
    /// the tunnel is the household's public address — it routes nowhere from inside
    /// the LAN and the forward simply times out. Only the address a device advertises
    /// over mDNS reaches its control server, and that is <see cref="Device.LanIp"/>.
    /// A device without one is unreachable, never a reason to try the public address.
    /// </summary>
    internal static (string? Host, int Port) ResolveLanTarget(Device tv) =>
        string.IsNullOrWhiteSpace(tv.LanIp)
            ? (null, TvControlPort)
            : (tv.LanIp, tv.LanPort ?? TvControlPort);

    // The LAN address comes from mDNS. Pointed at this machine, the proxy would reach the
    // loopback-only management API on behalf of any user.
    internal static bool IsDialableLanTarget(
        string host,
        IReadOnlyCollection<IPAddress> ownAddresses
    )
    {
        if (!IPAddress.TryParse(host, out IPAddress? address))
            return false;
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (
            IPAddress.IsLoopback(address)
            || address.Equals(IPAddress.Any)
            || address.Equals(IPAddress.IPv6Any)
            || address.IsIPv6LinkLocal
            || address.IsIPv6Multicast
        )
            return false;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] octets = address.GetAddressBytes();
            bool linkLocal = octets[0] == 169 && octets[1] == 254;
            if (octets[0] == 0 || linkLocal || octets[0] >= 224)
                return false;
        }

        return !ownAddresses.Any(own =>
            (own.IsIPv4MappedToIPv6 ? own.MapToIPv4() : own).Equals(address)
        );
    }

    private static IPAddress[] OwnAddresses() =>
        [
            .. NetworkInterface
                .GetAllNetworkInterfaces()
                .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
                .Select(unicast => unicast.Address),
        ];

    internal static HttpRequestMessage BuildForward(HttpContext context, string targetUrl)
    {
        HttpRequest request = context.Request;
        HttpRequestMessage forward = new(new HttpMethod(request.Method), targetUrl);

        if (request.ContentLength is > 0)
        {
            StreamContent content = new(request.Body);
            if (!string.IsNullOrEmpty(request.ContentType))
                content.Headers.TryAddWithoutValidation("Content-Type", request.ContentType);
            forward.Content = content;
        }

        // Forward the caller's own bearer token verbatim — the TV's /v1 accepts
        // the same Keycloak user token the phone uses; no re-mint needed.
        string? authorization = request.Headers.Authorization.FirstOrDefault();
        if (!string.IsNullOrEmpty(authorization))
            forward.Headers.TryAddWithoutValidation("Authorization", authorization);

        // Marks the forward as relayed, so LocalhostOnly refuses it if it ever lands here.
        forward.Headers.TryAddWithoutValidation(
            "X-Forwarded-For",
            context.ClientIp()?.ToString() ?? "unknown"
        );

        return forward;
    }

    // PATCH is not optional: every selection endpoint on the TV's /v1 surface —
    // subtitle, audio, quality, playlist, volume — is a PATCH. Omitting it made
    // the proxy answer 405 before the request ever reached the TV, so picking a
    // track from the web remote silently did nothing while the POST transport
    // controls (play/pause/seek) worked fine.
    [AcceptVerbs(["GET", "POST", "PUT", "PATCH", "DELETE"])]
    [Route("{deviceId}/{**path}")]
    public async Task<IActionResult> Proxy(string deviceId, string path)
    {
        Guid userId = User.UserId();
        logger.LogInformation(
            "[CastProxy] {Method} device={DeviceId} path=/{Path} user={User}",
            [Request.Method, deviceId, path, userId]
        );

        if (userId == Guid.Empty)
            return UnauthenticatedResponse("No authenticated user on the cast proxy request");

        Device? tv = await deviceRepository.GetOwnedDeviceAsync(deviceId, userId, "tv");

        if (tv is null)
        {
            logger.LogWarning(
                "[CastProxy] no owned TV device '{DeviceId}' for user {User}",
                [deviceId, userId]
            );
            return NotFoundResponse($"No owned TV device '{deviceId}' found");
        }

        (string? host, int port) = ResolveLanTarget(tv);

        if (host is null)
        {
            logger.LogWarning("[CastProxy] TV '{Name}' has no LAN IP", tv.Name);
            return ServiceUnavailableResponse("Target TV has no known LAN address");
        }

        // Transparent passthrough: the caller already includes the TV's API
        // version in the path (e.g. "v1/launch"), so forward it verbatim rather
        // than re-prefixing "/v1/" — doubling it (…/v1/v1/launch) 404s the TV.
        if (!IsDialableLanTarget(host, OwnAddresses()))
        {
            logger.LogWarning(
                "[CastProxy] refused TV '{Name}': {Ip} is not another device on the LAN",
                [tv.Name, host]
            );
            return ServiceUnavailableResponse("Target TV has no usable LAN address");
        }

        string query = Request.QueryString.HasValue ? Request.QueryString.Value! : string.Empty;
        string targetHost = host.Contains(':') ? $"[{host}]" : host;
        string targetUrl = $"http://{targetHost}:{port}/{path}{query}";
        logger.LogInformation("[CastProxy] forwarding to {TargetUrl}", targetUrl);

        using HttpRequestMessage forward = BuildForward(HttpContext, targetUrl);

        HttpClient client = httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(10);

        try
        {
            using HttpResponseMessage upstream = await client.SendAsync(
                forward,
                HttpCompletionOption.ResponseHeadersRead
            );

            byte[] body = await upstream.Content.ReadAsByteArrayAsync();
            logger.LogInformation(
                "[CastProxy] TV {Ip} replied {Status} ({Bytes} bytes)",
                [host, (int)upstream.StatusCode, body.Length]
            );

            Response.StatusCode = (int)upstream.StatusCode;
            Response.ContentType =
                upstream.Content.Headers.ContentType?.ToString() ?? "application/json";
            await Response.Body.WriteAsync(body);
            return new EmptyResult();
        }
        catch (TaskCanceledException)
        {
            logger.LogWarning("[CastProxy] TV '{Name}' ({Ip}) timed out", [tv.Name, host]);
            return GatewayTimeoutResponse($"TV '{tv.Name}' did not respond in time");
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(
                "[CastProxy] could not reach TV '{Name}' ({Ip}): {Message}",
                [tv.Name, host, ex.Message]
            );
            return ServiceUnavailableResponse($"Could not reach TV '{tv.Name}': {ex.Message}");
        }
    }
}
