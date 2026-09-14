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

using NoMercy.Database.Models.Users;

namespace NoMercy.Data.Repositories;

/// <summary>
/// Device rows read and written by live connections: the device-bus and the
/// playback handlers. Every call opens its own context, so a singleton may hold it.
/// </summary>
public interface IDeviceStateRepository
{
    Task<Guid?> GetOwnerAsync(Ulid deviceId);

    /// <summary>The owner's devices a picker offers: rows that still carry a fingerprint.</summary>
    Task<List<Device>> GetListedDevicesAsync(Guid ownerUserId);

    /// <summary>Every TV row the user owns, connected or not.</summary>
    Task<List<Device>> GetOwnedTvsAsync(Guid ownerUserId);

    /// <summary>
    /// The device row for <paramref name="fingerprint"/>, owned by <paramref name="userId"/>
    /// from now on, with the owner it had before (null for a new device).
    /// </summary>
    Task<(Device Device, Guid? PreviousOwner)> ClaimAsync(
        string fingerprint,
        Guid userId,
        string deviceName,
        string deviceType
    );

    /// <summary>
    /// Retires the unreachable rows an earlier identity of <paramref name="device"/> left
    /// behind. Returns true when any row was retired.
    /// </summary>
    Task<bool> RetireSupersededAsync(Device device, Guid ownerUserId, Func<Ulid, bool> isOnline);

    /// <summary>Stores the volume for every row carrying this client device id.</summary>
    Task SetVolumeAsync(string deviceId, int volumePercent);

    /// <summary>The device, when it exists and belongs to <paramref name="ownerUserId"/>.</summary>
    Task<Device?> GetOwnedAsync(Ulid id, Guid ownerUserId);

    /// <summary>Stores what a client device declared it can play. False when the device is unknown.</summary>
    Task<bool> SetCapabilitiesAsync(string deviceId, string capabilitiesJson);

    /// <summary>The user's unacknowledged device-drop notices, marked acknowledged as they are read.</summary>
    Task<List<DeviceDropNotice>> TakePendingDropNoticesAsync(Guid userId);
}
