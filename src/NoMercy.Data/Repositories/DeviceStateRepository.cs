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

using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using NoMercy.Database;
using NoMercy.Database.Models.Users;

namespace NoMercy.Data.Repositories;

public class DeviceStateRepository(IDbContextFactory<MediaContext> contextFactory)
    : IDeviceStateRepository
{
    public async Task<Guid?> GetOwnerAsync(Ulid deviceId)
    {
        await using MediaContext context = await contextFactory.CreateDbContextAsync();
        return await context
            .Devices.AsNoTracking()
            .Where(device => device.Id == deviceId)
            .Select(device => device.OwnerUserId)
            .FirstOrDefaultAsync();
    }

    public async Task<List<Device>> GetListedDevicesAsync(Guid ownerUserId)
    {
        await using MediaContext context = await contextFactory.CreateDbContextAsync();
        return await context
            .Devices.AsNoTracking()
            .Where(device => device.OwnerUserId == ownerUserId && device.Fingerprint != null)
            .ToListAsync();
    }

    public async Task<List<Device>> GetOwnedTvsAsync(Guid ownerUserId)
    {
        await using MediaContext context = await contextFactory.CreateDbContextAsync();
        return await context
            .Devices.AsNoTracking()
            .Where(device => device.OwnerUserId == ownerUserId && device.Type == "tv")
            .ToListAsync();
    }

    public async Task<(Device Device, Guid? PreviousOwner)> ClaimAsync(
        string fingerprint,
        Guid userId,
        string deviceName,
        string deviceType
    )
    {
        await using MediaContext context = await contextFactory.CreateDbContextAsync();
        return await ResolveOwnedDeviceAsync(context, fingerprint, userId, deviceName, deviceType);
    }

    public async Task<bool> RetireSupersededAsync(
        Device device,
        Guid ownerUserId,
        Func<Ulid, bool> isOnline
    )
    {
        await using MediaContext context = await contextFactory.CreateDbContextAsync();
        List<Device> candidates = await context
            .Devices.Where(SupersededCandidateFilter(device, ownerUserId))
            .ToListAsync();

        List<Device> retired = SelectSuperseded(candidates, isOnline);
        if (retired.Count == 0)
            return false;

        foreach (Device stale in retired)
            Retire(stale);

        await context.SaveChangesAsync();
        return true;
    }

    public async Task SetVolumeAsync(string deviceId, int volumePercent)
    {
        await using MediaContext context = await contextFactory.CreateDbContextAsync();
        await context
            .Devices.Where(device => device.DeviceId == deviceId)
            .ExecuteUpdateAsync(device => device.SetProperty(x => x.VolumePercent, volumePercent));
    }

    public async Task<Device?> GetOwnedAsync(Ulid id, Guid ownerUserId)
    {
        await using MediaContext context = await contextFactory.CreateDbContextAsync();
        return await context
            .Devices.AsNoTracking()
            .FirstOrDefaultAsync(device => device.Id == id && device.OwnerUserId == ownerUserId);
    }

    public async Task<bool> SetCapabilitiesAsync(string deviceId, string capabilitiesJson)
    {
        await using MediaContext context = await contextFactory.CreateDbContextAsync();
        Device? device = await context.Devices.FirstOrDefaultAsync(d => d.DeviceId == deviceId);
        if (device is null)
            return false;

        device.CapabilitiesJson = capabilitiesJson;
        await context.SaveChangesAsync();
        return true;
    }

    public async Task<List<DeviceDropNotice>> TakePendingDropNoticesAsync(Guid userId)
    {
        await using MediaContext context = await contextFactory.CreateDbContextAsync();
        List<DeviceDropNotice> notices = await context
            .DeviceDropNotices.Where(notice => notice.UserId == userId && !notice.Acknowledged)
            .ToListAsync();

        foreach (DeviceDropNotice notice in notices)
            notice.Acknowledged = true;
        await context.SaveChangesAsync();

        return notices;
    }

    /// <summary>
    /// The other rows that could be an earlier identity of <paramref name="device" />: same
    /// owner, same type, and the same name as the picker renders it.
    /// </summary>
    /// <remarks>
    /// The name compared is <c>CustomName ?? Name</c>, which is what the picker draws.
    /// Comparing raw <c>Name</c> missed the case this exists for — the rows an earlier
    /// identity leaves behind are written through different paths and carry different raw
    /// names, so two entries a user cannot tell apart were never recognised as one device
    /// and both kept being offered.
    ///
    /// An expression rather than an inline query so the rule itself can be tested, instead
    /// of only the reachability filter applied to its results.
    /// </remarks>
    public static Expression<Func<Device, bool>> SupersededCandidateFilter(
        Device device,
        Guid ownerUserId
    )
    {
        string effectiveName = device.CustomName ?? device.Name;
        Ulid deviceId = device.Id;
        string deviceType = device.Type;

        return candidate =>
            candidate.Id != deviceId
            && candidate.OwnerUserId == ownerUserId
            && candidate.Type == deviceType
            && (candidate.CustomName ?? candidate.Name) == effectiveName
            && candidate.Fingerprint != null;
    }

    /// <summary>
    /// Of the rows sharing this device's owner, name and type, the ones no longer
    /// reachable. A row still on the bus is a second, genuinely present device.
    /// </summary>
    public static List<Device> SelectSuperseded(
        IEnumerable<Device> candidates,
        Func<Ulid, bool> isOnline
    )
    {
        return [.. candidates.Where(candidate => !isOnline(candidate.Id))];
    }

    /// <summary>
    /// Take a row out of the picker without losing it. Clearing the fingerprint is
    /// what <c>GetDevices</c> filters on; the custom name, volume and history stay.
    /// </summary>
    public static void Retire(Device device)
    {
        device.Fingerprint = null;
        device.IsActive = false;
    }

    /// <summary>
    /// Resolves the device row for <paramref name="fingerprint"/> and pins its
    /// ownership to <paramref name="userId"/> — the account the device is currently
    /// authenticated as — creating the row when it does not exist. Returns the
    /// device together with the previous owner (<see langword="null"/> for a brand
    /// new device) so the caller can refresh a prior owner's list on a transfer.
    /// Ownership deliberately follows the session: a device logged into a new
    /// account must stop surfacing on the account that paired it first.
    /// </summary>
    public static async Task<(Device device, Guid? previousOwner)> ResolveOwnedDeviceAsync(
        MediaContext ctx,
        string fingerprint,
        Guid userId,
        string deviceName,
        string deviceType
    )
    {
        // DeviceId is globally unique (== ANDROID_ID for Android clients); the
        // Fingerprint fallback matches legacy device-bus rows that pre-date the ID
        // alignment. The lookup is intentionally NOT scoped by owner so a device
        // that moves accounts is found and re-owned rather than duplicated.
        Device? device = await ctx.Devices.FirstOrDefaultAsync(d =>
            d.DeviceId == fingerprint || d.Fingerprint == fingerprint
        );

        Guid? previousOwner = device?.OwnerUserId;

        if (device is null)
        {
            device = new()
            {
                DeviceId = fingerprint,
                Fingerprint = fingerprint,
                OwnerUserId = userId,
                Name = deviceName,
                Type = deviceType,
                IsActive = true,
            };
            ctx.Devices.Add(device);
        }
        else
        {
            device.Fingerprint = fingerprint;
            device.OwnerUserId = userId;
            if (string.IsNullOrEmpty(device.Type))
                device.Type = deviceType;
        }

        device.WsConnectedAt = DateTime.UtcNow;
        device.IsActive = true;
        await ctx.SaveChangesAsync();
        return (device, previousOwner);
    }
}
