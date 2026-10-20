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

using System.Security.Cryptography;
using System.Text;
using NoMercy.Database.Models.Users;
using NoMercy.Networking.Discovery;

namespace NoMercy.Networking.Devices;

/// <summary>
/// Builds the device list DeviceHub.GetDevices() and DeviceBusRegistry.
/// BroadcastChange() both send to clients. Shared here so the two call sites
/// — the SignalR pull and the push-on-connect/disconnect broadcast — can
/// never drift out of sync on how a registered row and a LAN-only Chromecast
/// are merged.
///
/// A registered <see cref="Device"/> row is listed once, real identity intact
/// (<see cref="DeviceListItem.IsRegisteredClient"/> = true). Any
/// <c>_googlecast._tcp</c> hit whose IP does not belong to a registered row
/// is ALSO listed — "the tv has chromecast and should always be listed as a
/// chromecast device" — but flagged IsRegisteredClient = false, with a
/// deterministic placeholder DeviceId/Fingerprint derived from Google's own
/// Cast device id rather than null: every existing client already treats
/// DeviceId/Fingerprint as non-null real-shaped strings, and turning them
/// nullable would break that assumption on first sight of an unregistered
/// Chromecast instead of just failing to recognise a new flag it doesn't
/// understand yet.
/// </summary>
public static class DeviceListComposer
{
    public static List<DeviceListItem> Compose(
        List<Device> registeredRows,
        Func<Ulid, bool> isOnline,
        Func<Ulid, (bool Foreground, bool ScreenOn)> getStatus,
        ICastMdnsRegistry castMdnsRegistry
    )
    {
        HashSet<string> registeredLanIps =
        [
            .. registeredRows.Where(d => !string.IsNullOrEmpty(d.LanIp)).Select(d => d.LanIp!),
        ];

        IReadOnlyCollection<CastMdnsHit> seen = castMdnsRegistry.GetSeen() ?? [];

        return
        [
            .. registeredRows.Select(d =>
            {
                (bool Foreground, bool ScreenOn) s = getStatus(d.Id);
                return new DeviceListItem
                {
                    DeviceId = d.Id,
                    Fingerprint = d.Fingerprint!,
                    Name = d.CustomName ?? d.Name,
                    Type = d.Type,
                    Online = isOnline(d.Id),
                    LanIp = d.LanIp,
                    LastSeenAt = d.WsConnectedAt > d.MdnsSeenAt ? d.WsConnectedAt : d.MdnsSeenAt,
                    Foreground = s.Foreground,
                    ScreenOn = s.ScreenOn,
                    CastReachable = castMdnsRegistry.IsReachable(d.LanIp),
                    IsRegisteredClient = true,
                };
            }),
            .. seen.Where(hit => !registeredLanIps.Contains(hit.Ip))
                .Select(ToUnregisteredCastDeviceListItem),
        ];
    }

    private static DeviceListItem ToUnregisteredCastDeviceListItem(CastMdnsHit hit) =>
        new()
        {
            DeviceId = DeterministicId(hit.Id),
            Fingerprint = $"cast:{hit.Id}",
            Name = hit.FriendlyName ?? hit.Model ?? "Chromecast device",
            Type = "chromecast",
            Online = false,
            LanIp = hit.Ip,
            LastSeenAt = hit.SeenAt,
            Foreground = false,
            ScreenOn = false,
            CastReachable = true,
            IsRegisteredClient = false,
        };

    // Same technique as ReclaimScanService.DeterministicId: hash a stable
    // string into a Ulid's 16 bytes so the same Cast device id always
    // produces the same placeholder DeviceId across polls/broadcasts,
    // without persisting a row for a device we may never own.
    public static Ulid DeterministicId(string castId)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"cast:{castId}"));
        return new Ulid([.. hash.AsSpan(0, 16)]);
    }
}
