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

using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NoMercy.Api.Services.Music;
using NoMercy.Authorization;
using NoMercy.Data.Repositories;
using NoMercy.Database.Models.Users;
using NoMercy.Networking.Messaging;

namespace NoMercy.Api.WebSockets;

[ApiController]
[Authorize]
[Route("ws/device-bus")]
public sealed class DeviceBusEndpoint(
    IDeviceStateRepository deviceStateRepository,
    IUserCache userCache,
    DeviceBusRegistry registry,
    MusicPlayerStateManager musicPlayerStateManager,
    MusicPlaybackService musicPlaybackService,
    ConnectedClients connectedClients,
    ILogger<DeviceBusEndpoint> logger
) : ControllerBase
{
    [HttpGet]
    public async Task Connect()
    {
        if (!HttpContext.WebSockets.IsWebSocketRequest)
        {
            HttpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        User? user = userCache.GetUser(HttpContext.User.UserId());
        if (user is null)
        {
            HttpContext.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        using WebSocket ws = await HttpContext.WebSockets.AcceptWebSocketAsync();
        await Pump(ws, user, HttpContext.RequestAborted);
    }

    private async Task Pump(WebSocket ws, User user, CancellationToken ct)
    {
        byte[] buffer = new byte[8192];
        Device? device = null;

        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                WebSocketReceiveResult result = await ws.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                string json = Encoding.UTF8.GetString(buffer, 0, result.Count);

                // Malformed payloads (split frames during stream churn,
                // unicode boundary issues, or buggy clients) used to throw
                // JsonException out of the loop and tear the whole device-
                // bus connection down. Skip the bad frame and wait for the
                // next clean message instead.
                JsonDocument doc;
                try
                {
                    doc = JsonDocument.Parse(json);
                }
                catch (JsonException)
                {
                    logger.LogDebug(
                        "device-bus skipping malformed frame ({Bytes} bytes)",
                        result.Count
                    );
                    continue;
                }

                using JsonDocument _ = doc;
                if (
                    !doc.RootElement.TryGetProperty("type", out JsonElement typeElement)
                    || typeElement.ValueKind != JsonValueKind.String
                )
                    continue;

                string? type = typeElement.GetString();

                if (type == "hello")
                {
                    device = await HandleHello(doc.RootElement, user, ws);
                    if (device is null)
                        break;
                }
                else if (type == "status" && device is not null)
                {
                    await HandleStatus(doc.RootElement, device);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "device-bus closed");
        }
        finally
        {
            if (device is not null)
                await ReleaseDevice(device);
        }
    }

    private async Task HandleStatus(JsonElement root, Device device)
    {
        bool foreground =
            root.TryGetProperty("foreground", out JsonElement fe)
            && fe.ValueKind == JsonValueKind.True;
        bool screenOn =
            root.TryGetProperty("screen_on", out JsonElement se)
            && se.ValueKind == JsonValueKind.True;

        registry.UpdateStatus(device.Id, foreground, screenOn);
        if (device.OwnerUserId is not null)
            await registry.BroadcastChange(device.OwnerUserId.Value);
    }

    /// <summary>Clears the active music claim when MusicHub agrees the device is gone, then unregisters it.</summary>
    private async Task ReleaseDevice(Device device)
    {
        if (
            device.OwnerUserId is not null
            && musicPlayerStateManager.TryGetValue(
                device.OwnerUserId.Value,
                out MusicPlayerState? playerState
            )
            && string.Equals(
                playerState.DeviceId,
                device.DeviceId,
                StringComparison.OrdinalIgnoreCase
            )
            && !IsStillOnMusicHub(connectedClients, device.DeviceId)
        )
        {
            // device-bus is a secondary wake/status channel (30s ping cadence,
            // TV-only, independent OkHttp socket) with its own reconnect churn —
            // it going down is NOT proof the device stopped playing. MusicHub's own
            // OnDisconnectedAsync already owns "is the active device really gone"
            // for playback purposes, complete with the KMP double-connect survivor
            // guard (see its otherConnectionForDeviceSurvives check). Clearing the
            // active claim here too, unconditionally, meant a bare device-bus blip
            // (a transient LAN hiccup, an OS-throttled background socket, mDNS churn
            // from another device on the network coming online) paused a device that
            // was still fully connected — and still playing — on MusicHub the whole
            // time. Only fall through when MusicHub agrees the device is gone too.
            logger.LogInformation(
                "Active music device {DeviceName} disconnected from device-bus — clearing active",
                device.Name
            );
            playerState.PlayState = false;
            playerState.DeviceId = null;
            User? owner = userCache.GetUser(device.OwnerUserId.Value);
            try
            {
                if (owner is not null)
                    await musicPlaybackService.UpdatePlaybackState(owner, playerState);
            }
            catch (Exception ex)
            {
                // Best-effort during teardown: the device still has to be unregistered.
                logger.LogDebug(ex, "device-bus teardown could not broadcast the cleared state");
            }
        }
        await registry.Unregister(device.Id);
    }

    /// <summary>
    /// True when <paramref name="deviceId"/> still has a live MusicHub connection —
    /// the authoritative "is this device actually still around for playback purposes"
    /// signal, independent of this device-bus socket's own lifecycle. See the call
    /// site in <see cref="Pump"/> for why device-bus going down must never be treated
    /// as proof of that on its own. Internal + static (rather than an instance
    /// method closing over the injected <see cref="ConnectedClients"/>) so it is
    /// directly unit-testable without standing up a live WebSocket.
    /// </summary>
    internal static bool IsStillOnMusicHub(ConnectedClients connectedClients, string deviceId)
    {
        return connectedClients.Clients.Values.Any(c =>
            c.DeviceId.Equals(deviceId, StringComparison.OrdinalIgnoreCase)
            && c.Endpoint.Contains("musicHub", StringComparison.OrdinalIgnoreCase)
        );
    }

    private async Task<Device?> HandleHello(JsonElement root, User user, WebSocket ws)
    {
        // Tolerate missing properties — legacy clients sometimes omit name /
        // device_type. Fingerprint is the only hard requirement.
        string? fingerprint = root.TryGetProperty("fingerprint", out JsonElement fpEl)
            ? fpEl.GetString()
            : null;
        if (string.IsNullOrEmpty(fingerprint))
            return null;

        string deviceName = root.TryGetProperty("name", out JsonElement nameEl)
            ? nameEl.GetString() ?? "Android TV"
            : "Android TV";
        string deviceType = root.TryGetProperty("device_type", out JsonElement typeEl)
            ? typeEl.GetString() ?? "tv"
            : "tv";

        (Device device, Guid? previousOwner) = await deviceStateRepository.ClaimAsync(
            fingerprint,
            user.Id,
            deviceName,
            deviceType
        );

        await registry.Register(device.Id, ws);

        await RetireSupersededRowsAsync(device, user.Id);

        // If the device just moved to a different account, refresh the previous
        // owner so the device disappears from their list immediately.
        if (previousOwner is { } previous && previous != user.Id)
            await registry.BroadcastChange(previous);
        return device;
    }

    /// <summary>
    /// Drop the rows an earlier identity of <paramref name="device" /> left behind,
    /// so one physical device is offered once.
    /// </summary>
    /// <remarks>
    /// A device that loses its stored id — a factory reset, a reinstall, or the
    /// signing-key change that rotates ANDROID_ID — hellos under a value nothing
    /// matches and gets a second row. Both rows carry a fingerprint, so the picker
    /// listed one TV twice under one name with nothing to tell the entries apart,
    /// and only the newest was ever on the bus: choosing the other sent a cast
    /// nowhere.
    ///
    /// The row is retired, not deleted. Clearing the fingerprint takes it out of
    /// <c>GetDevices</c> while its history, custom name and stored volume survive.
    /// A row currently registered on the bus is never touched, so two devices that
    /// genuinely share a name keep both entries as long as both are connected.
    /// </remarks>
    private async Task RetireSupersededRowsAsync(Device device, Guid ownerUserId)
    {
        if (
            !await deviceStateRepository.RetireSupersededAsync(
                device,
                ownerUserId,
                registry.IsOnline
            )
        )
            return;

        // Every mutation of Devices has to announce itself: the pickers only
        // replace their list on a push, so a silent retire leaves the entry on
        // screen until something unrelated fires the next one.
        await registry.BroadcastChange(ownerUserId);
    }
}
