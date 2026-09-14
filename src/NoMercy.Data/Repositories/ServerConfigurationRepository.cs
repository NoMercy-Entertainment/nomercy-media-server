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

using FlexLabs.EntityFrameworkCore.Upsert;
using Microsoft.EntityFrameworkCore;
using NoMercy.Database;

namespace NoMercy.Data.Repositories;

public class ServerConfigurationRepository(AppDbContext context) : IServerConfigurationRepository
{
    public async Task<string?> GetValueAsync(string key, CancellationToken ct = default)
    {
        return await context
            .Configuration.AsNoTracking()
            .Where(row => row.Key == key)
            .Select(row => row.Value)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<string> GetServerNameAsync(CancellationToken ct = default)
    {
        return await GetValueAsync(ServerConfigurationKeys.ServerName, ct)
            ?? Environment.MachineName;
    }

    public async Task SetValueAsync(
        string key,
        string value,
        Guid? modifiedBy,
        CancellationToken ct = default
    )
    {
        await context
            .Configuration.Upsert(
                new()
                {
                    Key = key,
                    Value = value,
                    ModifiedBy = modifiedBy,
                }
            )
            .On(row => row.Key)
            .WhenMatched(
                (stored, incoming) =>
                    new()
                    {
                        Value = incoming.Value,
                        ModifiedBy = incoming.ModifiedBy ?? stored.ModifiedBy,
                    }
            )
            .RunAsync(ct);
    }
}
