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

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using NoMercy.Api.Services.Cast;
using NoMercy.Api.WebSockets;
using NoMercy.Authorization;
using NoMercy.Database;
using NoMercy.Database.Activity;
using NoMercy.Database.Models.Users;
using NoMercy.Encoder.Devices;
using NoMercy.Networking;
using NoMercy.Networking.Devices;
using NoMercy.Networking.Discovery;
using NoMercy.Networking.Http;
using NoMercy.Networking.Messaging;
using NoMercy.Setup.Cast;

namespace NoMercy.Api.Hubs;

[Authorize]
public sealed class DeviceHub : ConnectionHub
{
    private readonly IDbContextFactory<MediaContext> _contextFactory;
    private readonly DeviceBusRegistry _busRegistry;
    private readonly IDeviceCapabilityRegistry _capabilityRegistry;
    private readonly ICastMdnsRegistry _castMdnsRegistry;
    private readonly ILogger<DeviceHub> _logger;
    private readonly IServerCastWaker _castWaker;

    public DeviceHub(
        IHttpContextAccessor httpContextAccessor,
        IDbContextFactory<MediaContext> contextFactory,
        ConnectedClients connectedClients,
        DeviceBusRegistry busRegistry,
        IActivityLogger activityLogger,
        IDeviceCapabilityRegistry capabilityRegistry,
        ICastMdnsRegistry castMdnsRegistry,
        IServerCastWaker castWaker,
        ILogger<DeviceHub> logger
    )
        : base(httpContextAccessor, contextFactory, connectedClients, activityLogger)
    {
        _contextFactory = contextFactory;
        _busRegistry = busRegistry;
        _capabilityRegistry = capabilityRegistry;
        _castMdnsRegistry = castMdnsRegistry;
        _castWaker = castWaker;
        _logger = logger;
    }

    private string? ResolveDeviceIdFromContext()
    {
        if (!ConnectedClients.Clients.TryGetValue(Context.ConnectionId, out Client? client))
            return null;
        return string.IsNullOrEmpty(client.DeviceId) ? null : client.DeviceId;
    }

    public async Task DeclareCapabilities(DeviceCapabilities payload)
    {
        string? deviceId = ResolveDeviceIdFromContext();
        if (deviceId is null)
            return; // unauthenticated or unknown — silently drop, never throw

        await using MediaContext ctx = await _contextFactory.CreateDbContextAsync();
        Device? device = await ctx.Devices.FirstOrDefaultAsync(d => d.DeviceId == deviceId);
        if (device is null)
            return;

        device.CapabilitiesJson = JsonConvert.SerializeObject(payload);
        await ctx.SaveChangesAsync();

        _capabilityRegistry.Set(deviceId, payload);

        _logger.LogInformation(
            "Device {DeviceId} declared capabilities: channels={Channels} codecs=[{Codecs}] ramTier={Tier}",
            [
                deviceId,
                payload.MaxAudioChannels,
                string.Join(",", payload.AudioCodecs),
                payload.RamTier,
            ]
        );
    }

    public async Task<List<DeviceListItem>> GetDevices()
    {
        User? user = UserCacheService.GetUser(Context.User.UserId());
        if (user is null)
            return [];

        await using MediaContext ctx = await _contextFactory.CreateDbContextAsync();
        List<Device> rows = await ctx
            .Devices.Where(d => d.OwnerUserId == user.Id && d.Fingerprint != null)
            .ToListAsync();

        return DeviceListComposer.Compose(
            rows,
            _busRegistry.IsOnline,
            _busRegistry.GetStatus,
            _castMdnsRegistry
        );
    }

    public async Task<WakeResult> WakeForMusic(string deviceId)
    {
        User? user = UserCacheService.GetUser(Context.User.UserId());
        if (user is null)
            return new("not_owned");

        if (!Ulid.TryParse(deviceId, out Ulid id))
            return new("not_owned");

        await using MediaContext ctx = await _contextFactory.CreateDbContextAsync();
        Device? device = await ctx.Devices.FindAsync(id);
        if (device is null || device.OwnerUserId != user.Id)
            return new("not_owned");

        if (_busRegistry.IsOnline(device.Id))
        {
            bool sent = await _busRegistry.SendAsync(
                device.Id,
                new { type = "wake_for_music", session_id = Guid.NewGuid().ToString() }
            );
            return new(sent ? "wake_sent" : "no_route");
        }

        // Off the bus: the server does the Cast wake itself rather than handing the
        // job back to whichever client happened to ask. `cast_fallback` made the
        // feature only as reliable as the weakest sender on the network — and left
        // any client without a Cast SDK unable to wake a TV at all.
        bool dispatched = await _castWaker.WakeAsync(device, user.Id, CastIntent.Idle());
        return new(dispatched ? "wake_sent" : "no_route");
    }

    public async Task<WakeResult> WakeForVideo(string deviceId)
    {
        User? user = UserCacheService.GetUser(Context.User.UserId());
        if (user is null)
            return new("not_owned");

        if (!Ulid.TryParse(deviceId, out Ulid id))
            return new("not_owned");

        await using MediaContext ctx = await _contextFactory.CreateDbContextAsync();
        Device? device = await ctx.Devices.FindAsync(id);
        if (device is null || device.OwnerUserId != user.Id)
            return new("not_owned");

        if (_busRegistry.IsOnline(device.Id))
        {
            bool sent = await _busRegistry.SendAsync(
                device.Id,
                new { type = "wake_for_video", session_id = Guid.NewGuid().ToString() }
            );
            return new(sent ? "wake_sent" : "no_route");
        }

        // Off the bus: the server does the Cast wake itself rather than handing the
        // job back to whichever client happened to ask. `cast_fallback` made the
        // feature only as reliable as the weakest sender on the network — and left
        // any client without a Cast SDK unable to wake a TV at all.
        bool dispatched = await _castWaker.WakeAsync(device, user.Id, CastIntent.Idle());
        return new(dispatched ? "wake_sent" : "no_route");
    }

    public async Task<List<DeviceDropNoticeDto>> PendingNotices()
    {
        User? user = UserCacheService.GetUser(Context.User.UserId());
        if (user is null)
            return [];

        await using MediaContext ctx = await _contextFactory.CreateDbContextAsync();
        List<DeviceDropNotice> notices = await ctx
            .DeviceDropNotices.Where(n => n.UserId == user.Id && !n.Acknowledged)
            .ToListAsync();

        foreach (DeviceDropNotice n in notices)
            n.Acknowledged = true;
        await ctx.SaveChangesAsync();

        return [.. notices.Select(n => new DeviceDropNoticeDto(n.DeviceName, n.Reason))];
    }

    public override async Task OnConnectedAsync()
    {
        await base.OnConnectedAsync();

        User? user = UserCacheService.GetUser(Context.User.UserId());
        if (user is null)
            return;

        // Wait briefly so the client's 'DeviceListChanged' handler is registered
        // before the broadcast lands. Mirrors the same fix on MusicHub — without
        // this delay the SignalR Java client drops the initial push because the
        // handler registration happens after the connection's Started callback.
        try
        {
            await Task.Delay(500, Context.ConnectionAborted);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        List<DeviceListItem> list = await GetDevices();

        // Broadcast to the WHOLE user group, not just the connecting client.
        // Clients.Caller only refreshed the newly-connected device's own picker;
        // every other already-connected device (e.g. a second TV, a phone) never
        // learned about the new device until it happened to reconnect itself.
        await Clients.User(user.Id.ToString()).SendAsync("DeviceListChanged", list);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        User? user = UserCacheService.GetUser(Context.User.UserId());

        await base.OnDisconnectedAsync(exception);

        if (user is null)
            return;

        List<DeviceListItem> list = await GetDevices();
        await Clients.User(user.Id.ToString()).SendAsync("DeviceListChanged", list);
    }
}
