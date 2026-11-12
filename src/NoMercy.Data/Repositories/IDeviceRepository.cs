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

public interface IDeviceRepository
{
    Task<List<Device>> GetDevices();

    Task AddDeviceAsync(Device device);

    Task DeleteDeviceAsync(Device device);

    /// <summary>Records that the owner forgot this device, so it cannot register again until a fresh sign-in.</summary>
    Task RevokeDeviceAsync(Device device, string? sessionId = null);

    Task<Device?> GetOwnerDeviceAsync(Ulid deviceId, Guid ownerUserId);

    Task<List<Device>> GetOwnerDevicesAsync(Guid ownerUserId);

    Task DeleteDeviceWithLogsAsync(Ulid deviceId);

    Task DeleteActivityLogsByOwnerAsync(Guid ownerUserId);

    Task<Device?> GetByIdAsync(Ulid deviceId);

    Task<List<Device>> GetAllAsync();

    Task DeleteAllActivityLogsAsync();

    /// <summary>
    /// The device a user owns with this client-reported device id and type, or null.
    /// </summary>
    Task<Device?> GetOwnedDeviceAsync(string deviceId, Guid ownerUserId, string type);

    /// <summary>The device registered under a client device id, whoever owns it.</summary>
    Task<Device?> GetByClientDeviceIdAsync(string deviceId);
}
