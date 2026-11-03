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
using NoMercy.Database.Models.Common;

namespace NoMercy.Database;

/// <summary>
/// Writes one Configuration value without ever failing on an existing key. A second
/// writer can insert the key between the read and the save; the loser then updates
/// the winner's row instead of throwing the unique-key violation.
/// </summary>
public static class ConfigurationUpsert
{
    public static async Task SetValueAsync(
        this AppDbContext db,
        string key,
        string value,
        Guid? modifiedBy = null,
        CancellationToken ct = default
    )
    {
        Configuration? existing = await db.Configuration.FirstOrDefaultAsync(c => c.Key == key, ct);
        if (existing is not null)
        {
            Apply(existing, value, modifiedBy);
            await db.SaveChangesAsync(ct);
            return;
        }

        Configuration inserted = new() { Key = key };
        Apply(inserted, value, modifiedBy);
        db.Configuration.Add(inserted);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.Entry(inserted).State = EntityState.Detached;
            existing = await db.Configuration.FirstOrDefaultAsync(c => c.Key == key, ct);
            if (existing is null)
                throw;

            Apply(existing, value, modifiedBy);
            await db.SaveChangesAsync(ct);
        }
    }

    public static void SetValue(
        this AppDbContext db,
        string key,
        string value,
        Guid? modifiedBy = null
    )
    {
        Configuration? existing = db.Configuration.FirstOrDefault(c => c.Key == key);
        if (existing is not null)
        {
            Apply(existing, value, modifiedBy);
            db.SaveChanges();
            return;
        }

        Configuration inserted = new() { Key = key };
        Apply(inserted, value, modifiedBy);
        db.Configuration.Add(inserted);
        try
        {
            db.SaveChanges();
        }
        catch (DbUpdateException)
        {
            db.Entry(inserted).State = EntityState.Detached;
            existing = db.Configuration.FirstOrDefault(c => c.Key == key);
            if (existing is null)
                throw;

            Apply(existing, value, modifiedBy);
            db.SaveChanges();
        }
    }

    private static void Apply(Configuration row, string value, Guid? modifiedBy)
    {
        row.Value = value;
        if (modifiedBy is not null)
            row.ModifiedBy = modifiedBy;
    }
}
