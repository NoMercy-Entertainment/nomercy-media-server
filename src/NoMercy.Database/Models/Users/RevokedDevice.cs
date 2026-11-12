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

using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;

namespace NoMercy.Database.Models.Users;

[PrimaryKey(nameof(Id))]
[Index(nameof(OwnerUserId), nameof(DeviceId), IsUnique = true)]
public class RevokedDevice
{
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    [JsonProperty("id")]
    public Ulid Id { get; set; } = Ulid.NewUlid();

    [JsonProperty("device_id")]
    public string DeviceId { get; set; } = string.Empty;

    [JsonProperty("owner_user_id")]
    public Guid OwnerUserId { get; set; }

    [JsonProperty("revoked_at")]
    public DateTime RevokedAt { get; set; } = DateTime.UtcNow;

    [JsonProperty("session_id")]
    public string? SessionId { get; set; }
}

public static class RevokedDeviceExtensions
{
    /// <summary>True while a forget stands; a later sign-in lifts it, a refreshed old token does not.</summary>
    public static async Task<bool> IsRevokedAsync(
        this MediaContext ctx,
        Guid ownerUserId,
        string deviceId,
        DateTime? authTime,
        string? sessionId = null
    )
    {
        RevokedDevice? revoked = await ctx.RevokedDevices.FirstOrDefaultAsync(r =>
            r.OwnerUserId == ownerUserId && r.DeviceId == deviceId
        );
        if (revoked is null)
            return false;

        // A stored sid is decisive; without one on either side the sign-in time decides.
        bool newSession =
            sessionId is not null && revoked.SessionId is not null
                ? sessionId != revoked.SessionId
                : authTime > revoked.RevokedAt;
        if (!newSession)
            return true;

        ctx.RevokedDevices.Remove(revoked);
        await ctx.SaveChangesAsync();
        return false;
    }
}
