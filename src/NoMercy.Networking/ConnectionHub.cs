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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using NoMercy.Authorization;
using NoMercy.Database;
using NoMercy.Database.Activity;
using NoMercy.Database.Models.Users;
using NoMercy.Networking.Http;
using NoMercy.Networking.Messaging;
using NoMercy.NmSystem.Extensions;
using NoMercy.NmSystem.SystemCalls;
using Serilog.Events;

namespace NoMercy.Networking;

[Authorize]
public class ConnectionHub : Hub
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IDbContextFactory<MediaContext> _contextFactory;
    protected IDbContextFactory<MediaContext> ContextFactory => _contextFactory;
    protected readonly ConnectedClients ConnectedClients;
    protected readonly IActivityLogger ActivityLogger;
    private string Endpoint { get; set; }

    protected IUserCache UserCacheService =>
        _httpContextAccessor.HttpContext?.RequestServices?.GetService<IUserCache>()
        ?? UserCache.Current;

    protected IMediaAuthorizationPolicy AuthPolicy =>
        _httpContextAccessor.HttpContext?.RequestServices?.GetService<IMediaAuthorizationPolicy>()
        ?? new MediaAuthorizationPolicy(UserCache.Current);

    protected ConnectionHub(
        IHttpContextAccessor httpContextAccessor,
        IDbContextFactory<MediaContext> contextFactory,
        ConnectedClients connectedClients,
        IActivityLogger activityLogger
    )
    {
        _httpContextAccessor = httpContextAccessor;
        _contextFactory = contextFactory;
        ConnectedClients = connectedClients;
        ActivityLogger = activityLogger;
        Endpoint = _httpContextAccessor.HttpContext?.Request.Path.Value ?? "Unknown";
        // Logger.Socket($"Connected to {Endpoint}");
    }

    public string GetCountryFromContext()
    {
        return _httpContextAccessor.HttpContext?.Request.Headers["country"].FirstOrDefault()
            ?? "US";
    }

    public string GetLanguageFromContext()
    {
        return _httpContextAccessor
                .HttpContext?.Request.Headers.AcceptLanguage.FirstOrDefault()
                ?.Split("_")
                .FirstOrDefault()
            ?? LocalizationHelper.GlobalLocalizer.TargetLanguage;
    }

    public override async Task OnConnectedAsync()
    {
        await base.OnConnectedAsync();

        User? user = UserCacheService.GetUser(Context.User.UserId());
        if (user is null)
        {
            // The "api" authorization policy already required this same UserCache
            // lookup to succeed before SignalR let this connection through — a miss
            // here means the cache diverged from the DB in the few milliseconds
            // since. Without this log this connection was silently never added to
            // ConnectedClients: every future SendTo/SendToAll for this user skips it
            // forever, with no error anywhere, until the client happens to reconnect.
            Logger.Socket(
                $"{Endpoint}: OnConnectedAsync found no cached user for sub {Context.User.UserId()} — connection will not receive broadcasts",
                LogEventLevel.Warning
            );
            return;
        }

        Client client = new()
        {
            Sub = user.Id,
            Ip = _httpContextAccessor.HttpContext?.ClientIp()?.ToString() ?? "Unknown",
            Socket = Clients.Caller,
            Endpoint = Endpoint,
            IsActive = true,
        };

        IQueryCollection? query = _httpContextAccessor.HttpContext?.Request.Query;
        if (query is not null && query.Count > 0)
        {
            if (query.TryGetValue("client_id", out StringValues value))
                client.DeviceId = value.ToString();

            if (query.TryGetValue("custom_name", out StringValues customName))
                client.CustomName = customName.ToString();

            if (
                query.TryGetValue("client_volume", out StringValues volumePercent)
                && int.TryParse(volumePercent.ToString(), out int parsedVolume)
            )
            {
                client.VolumePercent = Math.Clamp(parsedVolume, 0, 100);
            }

            if (query.TryGetValue("client_name", out StringValues name))
                client.Name = name.ToString();

            if (query.TryGetValue("client_type", out StringValues type))
                client.Type = type.ToString();

            if (query.TryGetValue("client_version", out StringValues version))
                client.Version = version.ToString();

            if (query.TryGetValue("client_os", out StringValues os))
                client.Os = os.ToString();

            if (query.TryGetValue("client_browser", out StringValues browser))
                client.Browser = browser.ToString();

            if (query.TryGetValue("client_device", out StringValues model))
                client.Model = model.ToString();
        }

        // client_id is the only field the upsert keys on — without it, every such
        // connection would collide on the same empty-string DeviceId row, so no
        // device is persisted and the id this client is carrying points at nothing.
        if (string.IsNullOrEmpty(client.DeviceId))
            ClearUnbackedDeviceId(client);

        if (!string.IsNullOrEmpty(client.DeviceId))
        {
            await using MediaContext mediaContext = await _contextFactory.CreateDbContextAsync();
            await mediaContext
                .Devices.Upsert(client)
                .On(x => x.DeviceId)
                .WhenMatched(
                    (ds, di) =>
                        new()
                        {
                            Browser = di.Browser,
                            DeviceId = di.DeviceId,
                            Ip = di.Ip,
                            Model = di.Model,
                            Name = di.Name,
                            Os = di.Os,
                            Type = di.Type,
                            Version = di.Version,
                            // VolumePercent intentionally NOT updated here: preserve the
                            // persisted per-device volume across (re)connections. Only an
                            // explicit SetDeviceVolumeCommand changes it. Otherwise the
                            // connect-time client_volume query param would clobber the stored
                            // level (resetting to the player's 100% default) on every reconnect.
                        }
                )
                .RunAsync();

            // Update CustomName separately — FlexLabs upsert doesn't support conditional expressions.
            // Only overwrite when the client sends a non-empty custom_name, preserving existing names otherwise.
            if (!string.IsNullOrEmpty(client.CustomName))
            {
                await mediaContext
                    .Devices.Where(x => x.DeviceId == client.DeviceId)
                    .ExecuteUpdateAsync(x => x.SetProperty(d => d.CustomName, client.CustomName));
            }

            Device? device = await mediaContext.Devices.FirstOrDefaultAsync(x =>
                x.DeviceId == client.DeviceId
            );

            AlignClientWithPersistedDevice(client, device);

            if (device is not null)
            {
                // WsConnectedAt was written only by the separate device-bus
                // WebSocket (DeviceBusEndpoint), never by a hub connection —
                // so a device controlled entirely through MusicHub/VideoHub/etc.
                // (the common case; the device-bus socket is its own,
                // independently flaky connection) never touched either signal
                // DeviceDropRuleCronJob reads (WsConnectedAt, MdnsSeenAt), and
                // got disowned as abandoned on its TTL/e-fuse rules despite
                // being actively used every day. Confirmed live, real TV,
                // 2026-09-09: "Tv in woonkamer" was dropped repeatedly, hours
                // apart, with WsConnectedAt permanently null and MdnsSeenAt
                // over a week stale, while under active real MusicHub control
                // the whole time. Every hub connection is real, current
                // evidence the device is still here — it should count.
                await mediaContext
                    .Devices.Where(x => x.DeviceId == device.DeviceId)
                    .ExecuteUpdateAsync(x =>
                        x.SetProperty(d => d.IsActive, true).SetProperty(d => d.WsConnectedAt, DateTime.UtcNow)
                    );
                await mediaContext.SaveChangesAsync();

                // Every hub derives from this one, so a single app opening video, music,
                // dashboard, device and cast hubs used to write five identical "connected"
                // rows a millisecond apart, and the activity log was mostly duplicates. The
                // event worth recording is the device arriving, which is the first of those
                // connections — the rest are the same device already here.
                //
                // The claim has to be atomic rather than a scan of the connection map: those
                // hubs connect concurrently, so each one would read the map before any of
                // them had written to it and every one would think it was first.
                if (ConnectedClients.RegisterDeviceConnection(client.DeviceId))
                    await ActivityLogger.LogConnectionAsync(
                        "connection.connected",
                        user.Id,
                        device.Id
                    );
            }
        }

        ConnectedClients.Clients.TryAdd(Context.ConnectionId, client);

        // Devices() is already filtered to this caller's user, so it must only go to
        // that user's own connections — Clients.All leaked one user's device names/IPs
        // to every connected client and corrupted the Connect device-switcher state.
        await Clients.User(user.Id.ToString()).SendAsync("ConnectedDevicesState", Devices());
    }

    private static void AlignClientWithPersistedDevice(Client client, Device? device)
    {
        // Align the in-memory client's PK with the persisted Devices.Id. The
        // Client base class assigns a fresh Ulid at construction; the upsert
        // matches on the DeviceId fingerprint and preserves the existing PK,
        // so client.Id and device.Id diverge. Subsequent ActivityLog writes
        // use device.Id from the in-memory map and FK-fail because the random
        // Ulid never made it into the Devices table.
        if (device is not null)
            client.Id = device.Id;
        else
            ClearUnbackedDeviceId(client);

        client.CustomName = device?.CustomName;
        client.VolumePercent = device?.VolumePercent;
        client.IsActive = true;
    }

    /// <summary>
    /// Drop the id of a client no Devices row backs, so nothing downstream
    /// presents it as one.
    /// </summary>
    /// <remarks>
    /// The <see cref="Device" /> base assigns a fresh Ulid at construction, which
    /// looks exactly like a key and is not one. A connection that never reached
    /// the upsert — no <c>client_id</c> on the query string, or a lookup that came
    /// back empty — still went into the connected-client map carrying it, and
    /// every activity row written against that client failed the foreign key and
    /// was dropped after three retries. One guest on a live server had not a
    /// single row recorded, ever, for that reason.
    ///
    /// Empty is the value the activity log already understands as "no device"
    /// (<c>ActivityLogger.WriteAsync</c> maps it to null, and the column is
    /// nullable for exactly this). Saying that records the event with the device
    /// left blank instead of losing the event.
    /// </remarks>
    internal static void ClearUnbackedDeviceId(Client client)
    {
        client.Id = Ulid.Empty;
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await base.OnDisconnectedAsync(exception);

        if (ConnectedClients.Clients.TryGetValue(Context.ConnectionId, out Client? client))
        {
            await using MediaContext mediaContext = await _contextFactory.CreateDbContextAsync();
            Device? device = await mediaContext.Devices.FirstOrDefaultAsync(x =>
                x.DeviceId == client.DeviceId
            );
            if (device is not null)
            {
                await mediaContext
                    .Devices.Where(x => x.DeviceId == device.DeviceId)
                    .ExecuteUpdateAsync(x => x.SetProperty(d => d.IsActive, false));
                await mediaContext.SaveChangesAsync();
            }

            ConnectedClients.Clients.Remove(Context.ConnectionId, out _);

            // Mirror of the connect side: the device has only actually left once its last
            // hub connection is gone. Logging per closed connection turned one app closing
            // into five "disconnected" rows.
            bool deviceLeft = ConnectedClients.ReleaseDeviceConnection(client.DeviceId);

            if (device is not null && deviceLeft)
                await ActivityLogger.LogConnectionAsync(
                    "connection.disconnected",
                    client.Sub,
                    device.Id
                );

            // Scope to the disconnecting client's own user (see OnConnectedAsync).
            await Clients
                .User(Context.User.UserId().ToString())
                .SendAsync("ConnectedDevicesState", Devices());
        }
    }

    public List<Device> Devices()
    {
        User? user = UserCacheService.GetUser(Context.User.UserId());
        if (user is null)
            return [];

        return ConnectedClients
            .Clients.Values.Where(x => x.Sub.Equals(user.Id))
            .Where(x => x.Endpoint == Endpoint)
            .Select(c => new Device
            {
                Name = c.Name,
                Ip = c.Ip,
                DeviceId = c.DeviceId,
                Browser = c.Browser,
                Os = c.Os,
                Model = c.Model,
                Type = c.Type,
                Version = c.Version,
                Id = c.Id,
                CustomName = c.CustomName,
                VolumePercent = c.VolumePercent,
            })
            .ToList();
    }
}
