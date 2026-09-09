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

using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using NoMercy.Api.Hubs;
using NoMercy.Database;
using NoMercy.Database.Models.Users;
using NoMercy.Networking.Devices;
using NoMercy.Networking.Discovery;

namespace NoMercy.Api.WebSockets;

public sealed class DeviceBusRegistry(
    IDbContextFactory<MediaContext> contextFactory,
    IHubContext<DeviceHub> hubContext,
    ICastMdnsRegistry castMdnsRegistry
) : IDeviceListChangeNotifier
{
    private readonly ConcurrentDictionary<Ulid, WebSocket> _live = new();

    // Per-device foreground/screen state reported by the device-bus client.
    // Phone-side picker reads these via DeviceListItem to decide whether to
    // fire the Cast SDK CEC wake (skip when both are true — panel is on with
    // our app already on screen, no wake needed).
    private readonly ConcurrentDictionary<Ulid, (bool Foreground, bool ScreenOn)> _status = new();

    public void UpdateStatus(Ulid deviceId, bool foreground, bool screenOn)
    {
        _status[deviceId] = (foreground, screenOn);
    }

    public (bool Foreground, bool ScreenOn) GetStatus(Ulid deviceId) =>
        _status.TryGetValue(deviceId, out (bool Foreground, bool ScreenOn) s) ? s : (false, false);

    public async Task Register(Ulid deviceId, WebSocket ws)
    {
        _live[deviceId] = ws;

        await using MediaContext ctx = await contextFactory.CreateDbContextAsync();
        Device? device = await ctx.Devices.FindAsync(deviceId);
        if (device?.OwnerUserId is not null)
            await BroadcastChange(device.OwnerUserId.Value);
    }

    public async Task Unregister(Ulid deviceId)
    {
        _live.TryRemove(deviceId, out _);
        _status.TryRemove(deviceId, out _);

        await using MediaContext ctx = await contextFactory.CreateDbContextAsync();
        Device? device = await ctx.Devices.FindAsync(deviceId);
        if (device is null)
            return;

        // WsConnectedAt is not "is the device-bus socket open right now" — it's
        // DeviceDropRuleCronJob's and DeviceListComposer's only record of when
        // this device was last known alive at all, across every connection
        // type (ConnectionHub.OnConnectedAsync writes it from every hub, not
        // just this one). Nulling it here on an ordinary, expected disconnect
        // wiped that history every time the device-bus socket dropped — which
        // it does often and normally — even while the device stayed live and
        // in active use over MusicHub. That falsely read as "never seen" to
        // the drop rule and as a blank "last seen" to the device list.
        // Confirmed live, real TV, 2026-09-09: "Tv in woonkamer" was disowned
        // as abandoned, repeatedly, hours apart, while under continuous real
        // MusicHub control the whole time.

        if (device.OwnerUserId is not null)
            await BroadcastChange(device.OwnerUserId.Value);
    }

    public bool IsOnline(Ulid deviceId) => _live.ContainsKey(deviceId);

    public async Task<bool> SendAsync(Ulid deviceId, object payload, CancellationToken ct = default)
    {
        if (!_live.TryGetValue(deviceId, out WebSocket? ws) || ws.State != WebSocketState.Open)
            return false;

        string json = JsonSerializer.Serialize(payload);
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
        return true;
    }

    public void Touch(Ulid deviceId)
    {
        // pong received — presence confirmed by socket remaining in _live
    }

    public void ForceClose(Ulid deviceId)
    {
        if (_live.TryRemove(deviceId, out WebSocket? ws) && ws.State == WebSocketState.Open)
            ws.Abort();
    }

    public async Task BroadcastChange(Guid ownerUserId)
    {
        await using MediaContext ctx = await contextFactory.CreateDbContextAsync();
        List<Device> rows = await ctx
            .Devices.Where(d => d.OwnerUserId == ownerUserId && d.Fingerprint != null)
            .ToListAsync();

        List<DeviceListItem> items = DeviceListComposer.Compose(
            rows,
            IsOnline,
            GetStatus,
            castMdnsRegistry
        );

        await hubContext.Clients.User(ownerUserId.ToString()).SendAsync("DeviceListChanged", items);
    }
}
