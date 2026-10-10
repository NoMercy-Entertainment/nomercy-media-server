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
using NoMercy.Networking.Http;

namespace NoMercy.Networking.Messaging;

public class ConnectedClients
{
    public ConcurrentDictionary<string, Client> Clients { get; } = new();

    /// <summary>
    /// The live connections a user holds on one hub, and the single definition of
    /// "reachable over SignalR" that <see cref="ClientMessenger.SendTo" /> and every
    /// reachability check share.
    /// </summary>
    /// <remarks>
    /// A client registers one connection per hub, so matching on the user alone
    /// reports a user on <c>musicHub</c> as reachable on <c>videoHub</c> and the send
    /// then hits nothing. <see cref="Client.Endpoint" /> is stored with the leading
    /// slash SignalR routes on, which the caller's hub name does not carry.
    /// </remarks>
    public List<KeyValuePair<string, Client>> ConnectionsFor(Guid userId, string hub) =>
        [
            .. Clients.Where(connection =>
                connection.Value.Sub.Equals(userId) && connection.Value.Endpoint == "/" + hub
            ),
        ];

    public bool IsReachable(Guid userId, string hub) => ConnectionsFor(userId, hub).Count > 0;

    /// <summary>
    /// How many live hub connections each device currently holds.
    /// </summary>
    /// <remarks>
    /// Every hub in the app derives from the same base, so one client opens five or six of
    /// them at once. <see cref="Clients"/> is keyed by connection and cannot answer "is this
    /// device already here" without a race: the hubs connect within milliseconds of each
    /// other, and each one reads the dictionary before any of them has written to it.
    /// </remarks>
    private readonly ConcurrentDictionary<string, int> _connectionsPerDevice = new();
    private readonly object _deviceConnectionLock = new();

    /// <summary>
    /// Registers one hub connection for a device and reports whether the device has just
    /// arrived — that is, whether this is its only connection.
    /// </summary>
    public bool RegisterDeviceConnection(string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId))
            return false;

        lock (_deviceConnectionLock)
            return _connectionsPerDevice.AddOrUpdate(deviceId, 1, (_, count) => count + 1) == 1;
    }

    /// <summary>
    /// Releases one hub connection for a device and reports whether the device has actually
    /// left — that is, whether that was the last one.
    /// </summary>
    public bool ReleaseDeviceConnection(string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId))
            return false;

        lock (_deviceConnectionLock)
        {
            int remaining = _connectionsPerDevice.AddOrUpdate(deviceId, 0, (_, count) => count - 1);

            if (remaining > 0)
                return false;

            // Nobody left holding it, so stop tracking it. A negative count would mean more
            // releases than registrations, which is still a departure as far as callers care.
            _connectionsPerDevice.TryRemove(deviceId, out int _);
            return true;
        }
    }
}
