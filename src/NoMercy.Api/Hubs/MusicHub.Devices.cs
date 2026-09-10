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

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NoMercy.Api.Services.Music;
using NoMercy.Authorization;
using NoMercy.Database;
using NoMercy.Database.Models.Users;
using NoMercy.Networking.Cast;
using NoMercy.Networking.Http;
using NoMercy.NmSystem.Information;
using NoMercy.Setup.Cast;

namespace NoMercy.Api.Hubs;

public partial class MusicHub
{
    /// <summary>
    /// Returns the Client belonging to the connection that invoked this hub
    /// method. Does NOT mutate the active-device registry — use this when you
    /// need to log who triggered an action but do not want to promote them to
    /// active.
    /// </summary>
    private Device GetCallerDevice(User user)
    {
        if (!ConnectedClients.Clients.TryGetValue(Context.ConnectionId, out Client? device))
            throw new InvalidOperationException(
                $"Connection {Context.ConnectionId} not found in ConnectedClients"
            );
        return device;
    }

    /// <summary>
    /// Returns the user's current active device. If no active device is
    /// recorded, or the recorded device has disconnected, the caller is
    /// promoted to active. Otherwise the existing active is preserved —
    /// passive callers do NOT steal active away from a live target.
    /// </summary>
    private Device GetOrPromoteActiveDevice(User user)
    {
        Device caller = GetCallerDevice(user);

        if (_activeDeviceRegistry.TryGet(user.Id, out Device? existing) && existing is not null)
        {
            bool existingStillConnected = ConnectedClients.Clients.Values.Any(c =>
                c.DeviceId.Equals(existing.DeviceId, StringComparison.OrdinalIgnoreCase)
            );
            if (existingStillConnected)
                return existing;
        }

        _activeDeviceRegistry.Set(user.Id, caller);
        return caller;
    }

    /// <summary>
    /// MusicHub-flavoured device list: live MusicHub clients plus every TV the
    /// current user owns from the Devices table, including ones that aren't
    /// currently on the hub (sleeping panels, powered-off boxes). The web and
    /// mobile pickers need to render those so the user can wake them; without
    /// this merge a standby TV silently disappears from the picker until it
    /// reconnects on its own.
    /// </summary>
    private async Task<List<Device>> MusicDevicesAsync()
    {
        List<Device> connected = Devices();
        User? user = UserCacheService.GetUser(Context.User.UserId());
        if (user is null)
            return connected;

        await using MediaContext ctx = await ContextFactory.CreateDbContextAsync();
        List<Device> registeredTvs = await ctx
            .Devices.Where(d => d.OwnerUserId == user.Id && d.Type == "tv")
            .ToListAsync();

        HashSet<string> seenDeviceIds = new(
            connected.Select(d => d.DeviceId),
            StringComparer.OrdinalIgnoreCase
        );

        foreach (Device tv in registeredTvs)
        {
            if (seenDeviceIds.Add(tv.DeviceId))
                connected.Add(tv);
        }

        // Pre-warm sharpcaster's TLS pool for every owned TV so the first
        // ChangeDeviceCommand to that TV doesn't pay cold-handshake latency
        // (which is the leading cause of first-tap LAUNCH races on the wire).
        // Fire-and-forget; the per-receiver client pool dedupes so repeated
        // calls are cheap.
        foreach (Device tv in registeredTvs)
        {
            string? resolved = CastAddress.Resolve(tv.LanIp, tv.Ip);
            if (resolved is null)
                continue;

            string ip = resolved;
            _ = Task.Run(async () =>
            {
                try
                {
                    string? receiverName = await _chromeCast.FindReceiverNameByIpAsync(ip);
                    if (!string.IsNullOrEmpty(receiverName))
                        await _chromeCast.SelectChromecast(receiverName);
                }
                catch
                {
                    // Best-effort pre-warm; the actual cast will retry the
                    // discovery + connect path itself if this fails.
                }
            });
        }

        return connected;
    }

    private void UpdateDeviceInfo(MusicPlayerState state)
    {
        if (!ConnectedClients.Clients.TryGetValue(Context.ConnectionId, out Client? device))
            return;

        // Only adopt the caller's device as active when there is no active
        // device yet, or when the caller IS the current active. A passive
        // device that initiates a playlist change (e.g. phone tapping an
        // album while music plays on the TV) must NOT steal active back —
        // the new playlist should land on the existing active device.
        bool callerIsActiveOrNoActive =
            string.IsNullOrEmpty(state.DeviceId)
            || state.DeviceId.Equals(device.DeviceId, StringComparison.OrdinalIgnoreCase);

        if (callerIsActiveOrNoActive)
        {
            state.DeviceId = device.DeviceId;
            state.VolumePercentage = device.VolumePercent ?? Device.DefaultVolumePercent;
        }

        UpdateDeviceVolumes(state, device.Sub);
    }

    private void UpdateDeviceVolumes(MusicPlayerState state, Guid userSub)
    {
        Dictionary<string, int> volumes = new();
        foreach (Client client in ConnectedClients.Clients.Values)
        {
            if (!client.Sub.Equals(userSub))
                continue;
            volumes[client.DeviceId] = client.VolumePercent ?? Device.DefaultVolumePercent;
        }

        state.DeviceVolumes = volumes;
    }

    public async Task ChangeDeviceCommand(string? deviceId)
    {
        User? user = UserCacheService.GetUser(Context.User.UserId());
        if (user is null)
            return;

        if (string.IsNullOrEmpty(deviceId))
        {
            await HandleReleaseActiveClaimCommand(user);
            return;
        }

        List<Device> connectedDevices = await MusicDevicesAsync();

        Device? targetTv = connectedDevices.FirstOrDefault(d =>
            d.DeviceId.Equals(deviceId, StringComparison.OrdinalIgnoreCase) && d.Type == "tv"
        );

        // If the target is a TV that owns the user but isn't currently on
        // MusicHub, fire wake_for_music over the device-bus so its panel +
        // app come up. Without this, the web picker can transfer the active
        // flag to a sleeping TV but the TV never actually plays. Mobile
        // already drives this through DeviceHub.WakeForMusic; web can't, so
        // the server has to do it on their behalf.
        //
        // A live MusicHub connection only proves the process is alive, not
        // that its screen is on and foregrounded — a backgrounded app keeps
        // its socket open indefinitely. Measured 2026-09-08: the bedroom TV
        // was MusicHub-live, screen off, activity backgrounded; targetIsLive
        // read true, both the software wake and the Cast panel-wake LAUNCH
        // below were skipped, and nothing brought the screen up. GetStatus's
        // Foreground bit (the client's own /v1/ping) is the real signal —
        // require it too.
        bool targetIsLive =
            connectedDevices.Any(d =>
                d.DeviceId.Equals(deviceId, StringComparison.OrdinalIgnoreCase)
                && ConnectedClients.Clients.Values.Any(c =>
                    c.DeviceId.Equals(deviceId, StringComparison.OrdinalIgnoreCase)
                    && c.Endpoint.Contains("musicHub", StringComparison.OrdinalIgnoreCase)
                )
            )
            && (targetTv is null || _busRegistry.GetStatus(targetTv.Id).Foreground);

        if (targetTv is not null)
        {
            // Software wake: only when the TV's MusicHub side isn't already live.
            // If it's live the app is already foregrounded; sending wake_for_music
            // again would just redundantly bounce the activity stack.
            if (!targetIsLive && _busRegistry.IsOnline(targetTv.Id))
            {
                _ = _busRegistry.SendAsync(
                    targetTv.Id,
                    new { type = "wake_for_music", session_id = Guid.NewGuid().ToString() }
                );
            }

            // Panel wake (CEC OTP) via a server-side Cast LAUNCH. CastPanelWakeLauncher
            // owns the cold-target-only gate (ShouldFireCastWake) plus the LAUNCH
            // itself, resolving the receiver via its LAN IP rather than name — the
            // Cast mDNS name (set in Android TV settings) doesn't match our DB's
            // custom name (set in NoMercy onboarding). Best-effort, async — some
            // cast_shell builds don't honor third-party LAUNCHes.
            string? targetIp = CastAddress.Resolve(targetTv.LanIp, targetTv.Ip);
            Ulid targetUlid = targetTv.Id;
            string serverIdString = Info.DeviceId.ToString();
            string serverUrl = ResolveServerUrl();
            string locale = ResolveSenderLocale();
            CastIntent intent = ResolveMusicIntent(user.Id, deviceId);
            bool apkOnline = _busRegistry.IsOnline(targetUlid);

            _ = Task.Run(() =>
                _castPanelWakeLauncher.LaunchIfColdAsync(
                    targetIsLive,
                    targetIp,
                    apkOnline,
                    () =>
                        _castTokenService.MintAsync(
                            userId: user.Id,
                            serverId: serverIdString,
                            serverUrl: serverUrl,
                            deviceId: targetUlid,
                            intent: intent,
                            clientLocale: locale
                        ),
                    isTargetOnlineNow: () => _busRegistry.IsOnline(targetUlid)
                )
            );
        }

        if (_musicPlayerStateManager.TryGetValue(user.Id, out MusicPlayerState? playerState))
        {
            playerState.DeviceId = deviceId;

            // Volume is owned by the device: the device being transferred TO plays
            // at its own remembered level, never at whatever the outgoing active
            // device happened to be at. ResolveTransferVolume prefers a level
            // already known in DeviceVolumes, then the device's own persisted
            // VolumePercent, then the shared safe default.
            Device? targetDevice = connectedDevices.FirstOrDefault(d =>
                d.DeviceId.Equals(deviceId, StringComparison.OrdinalIgnoreCase)
            );
            playerState.VolumePercentage = MusicVolumeResolver.ResolveTransferVolume(
                playerState,
                deviceId,
                targetDevice?.VolumePercent
            );
            playerState.DeviceVolumes[deviceId] = playerState.VolumePercentage;

            // A manual switch always gives the newly-promoted device a fresh grace
            // window — without this, a target that hasn't sent its first position
            // report yet could inherit the previous device's stale heartbeat and
            // get force-ended by MusicPlaybackService before it ever gets a chance
            // to prove it's alive.
            playerState.LastActiveHeartbeatUtc = DateTime.UtcNow;

            // The incoming device has proven nothing yet, so it gets the longer
            // first-report window rather than the one meant for finding a device
            // that died mid-playback. A TV woken from sleep spends most of that
            // window booting. See MusicPlaybackService.IsActiveDeviceStale.
            playerState.ActiveDeviceProvenAlive = false;

            // Time itself doesn't change on a transfer, but the capture instant
            // must — the incoming device needs its own fresh drift-free reference
            // point rather than inheriting one that predates its own handoff.
            playerState.SetPosition(playerState.Time);
        }
        else
        {
            // No live player state — nothing to transfer. The else-branch's previous
            // `UpdatePlaybackState(user, null)` call would have NRE'd; just return.
            return;
        }

        // Keep the active-device registry in sync with playerState.DeviceId.
        // Without this, the registry could still point at whoever last promoted
        // themselves (e.g. the web client that initiated this ChangeDevice), while
        // playerState says TV — and downstream calls that consult the registry
        // would see a stale active.
        Device? targetClient = ConnectedClients.Clients.Values.FirstOrDefault(c =>
            c.DeviceId.Equals(deviceId, StringComparison.OrdinalIgnoreCase)
        );
        if (targetClient is not null)
            _activeDeviceRegistry.Set(user.Id, targetClient);

        // Relay-first: the new active device starts playing off this the moment
        // it arrives, so it goes out before the two broadcasts below — those are
        // pure bookkeeping (device-list refresh, old-device status notice) and
        // don't gate anything the target needs in order to begin playback.
        await _musicPlaybackService.UpdatePlaybackState(user, playerState);

        EventPayload<BroadcastEventPayload> payload = new()
        {
            Events =
            [
                new()
                {
                    DeviceBroadcastStatus = new()
                    {
                        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        BroadcastStatus = MusicEventType.BroadcastUnavailable,
                        DeviceId = deviceId,
                    },
                },
            ],
        };

        await _clientMessenger.SendTo("ChangeDevice", "musicHub", user.Id, payload);

        await _clientMessenger.SendTo(
            "ConnectedDevicesState",
            "musicHub",
            user.Id,
            connectedDevices
        );
    }

    /// <summary>
    /// A null/empty deviceId on <see cref="ChangeDeviceCommand"/> means "release my active
    /// claim" — e.g. a TV's standby job backgrounding itself — not "stop everything". Only the
    /// device the server currently considers active may release its own claim; a passive caller
    /// sending null/empty is a no-op, same as before this existed, so it can never end someone
    /// else's claim by accident. Clearing DeviceId (mirrors
    /// <see cref="MusicPlaybackService.EndStaleActiveSessionAsync"/> and the graceful-release
    /// branch of <see cref="OnDisconnectedAsync"/>) is what lets the very next
    /// StartPlaybackCommand from ANY connected device win active immediately. CurrentItem,
    /// Playlist, Backlog and position all survive untouched — this is the device going dark,
    /// not the session ending, so the user can resume this exact spot elsewhere. Deliberately
    /// does not touch cast-wake: releasing a claim should never launch a receiver.
    /// </summary>
    private async Task HandleReleaseActiveClaimCommand(User user)
    {
        if (!ConnectedClients.Clients.TryGetValue(Context.ConnectionId, out Client? caller))
            return;

        if (!_musicPlayerStateManager.TryGetValue(user.Id, out MusicPlayerState? playerState))
            return;

        bool callerIsActiveDevice =
            !string.IsNullOrEmpty(playerState.DeviceId)
            && playerState.DeviceId.Equals(caller.DeviceId, StringComparison.OrdinalIgnoreCase);

        if (!callerIsActiveDevice)
        {
            _logger.LogWarning(
                "{Name}: [MusicHub.ChangeDeviceCommand] release ignored — caller is not the active device",
                user.Name
            );
            return;
        }

        _musicPlaybackService.RemoveTimer(user.Id);
        _activeDeviceRegistry.RemoveIfMatches(user.Id, caller.DeviceId);

        playerState.PlayState = false;
        playerState.DeviceId = null;
        UpdateActionsDisallows(playerState);

        await _musicPlaybackService.UpdatePlaybackState(user, playerState);
    }

    public async Task ChangeVolumeCommand(int? volume)
    {
        await SetDeviceVolumeCommand(null, volume);
    }

    public async Task SetDeviceVolumeCommand(string? deviceId, int? volume)
    {
        User? user = UserCacheService.GetUser(Context.User.UserId());
        if (user is null)
            return;

        if (volume is null)
            return;

        int clamped = MusicVolumeResolver.Clamp(volume.Value);

        Device? target = ResolveVolumeTarget(user.Id, deviceId);
        if (target is null)
            return;

        _activeDeviceRegistry.TryGet(user.Id, out Device? active);

        target.VolumePercent = clamped;

        if (_musicPlayerStateManager.TryGetValue(user.Id, out MusicPlayerState? playerState))
        {
            // The scoped volume_percentage belongs to the active device; only
            // move it when the active device is the one being changed. Either
            // way the device_volumes map always gets the caller's target entry
            // so controller sliders update.
            MusicVolumeResolver.ApplyDeviceVolume(
                playerState,
                target.DeviceId,
                active?.DeviceId,
                clamped
            );
            await _musicPlaybackService.UpdatePlaybackState(user, playerState);
        }

        // Persist off the critical path — the broadcast already reached clients.
        // An in-line await on ExecuteUpdateAsync added 500+ms of wire latency
        // per volume event on SQLite under load.
        string targetDeviceId = target.DeviceId;
        _ = Task.Run(async () =>
        {
            try
            {
                await using MediaContext mediaContext = await ContextFactory.CreateDbContextAsync();
                await mediaContext
                    .Devices.Where(d => d.DeviceId == targetDeviceId)
                    .ExecuteUpdateAsync(d => d.SetProperty(x => x.VolumePercent, clamped));
            }
            catch (Exception ex)
            {
                _logger.LogInformation(
                    "SetDeviceVolumeCommand DB persist failed: {Message}",
                    ex.Message
                );
            }
        });
    }

    private Device? ResolveVolumeTarget(Guid userId, string? deviceId)
    {
        if (string.IsNullOrEmpty(deviceId))
            return _activeDeviceRegistry.TryGet(userId, out Device? active) ? active : null;

        return ConnectedClients.Clients.Values.FirstOrDefault(client =>
            client.Sub.Equals(userId)
            && client.DeviceId.Equals(deviceId, StringComparison.OrdinalIgnoreCase)
        );
    }
}
