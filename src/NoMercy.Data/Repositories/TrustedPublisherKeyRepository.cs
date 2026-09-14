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
using NoMercy.Database;
using NoMercy.Database.Models.Media;

namespace NoMercy.Data.Repositories;

public class TrustedPublisherKeyRepository(MediaContext context) : ITrustedPublisherKeyRepository
{
    public Task<List<TrustedPublisherKey>> GetAllAsync(CancellationToken ct = default)
    {
        return context.TrustedPublisherKeys.AsNoTracking().OrderBy(k => k.AddedAt).ToListAsync(ct);
    }

    public Task<bool> ExistsAsync(string fingerprint, CancellationToken ct = default)
    {
        return context
            .TrustedPublisherKeys.AsNoTracking()
            .AnyAsync(k => k.Fingerprint == fingerprint, ct);
    }

    public async Task AddAsync(TrustedPublisherKey key, CancellationToken ct = default)
    {
        context.TrustedPublisherKeys.Add(key);
        await context.SaveChangesAsync(ct);
    }

    public async Task<bool> DeleteAsync(string fingerprint, CancellationToken ct = default)
    {
        int deleted = await context
            .TrustedPublisherKeys.Where(k => k.Fingerprint == fingerprint)
            .ExecuteDeleteAsync(ct);
        return deleted > 0;
    }
}
