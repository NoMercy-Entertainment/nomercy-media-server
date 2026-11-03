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
using NoMercyQueue.Core.Interfaces;
using DbConfiguration = NoMercy.Database.Models.Common.Configuration;

namespace NoMercy.Queue.MediaServer.Configuration;

public class MediaConfigurationStore(IDbContextFactory<AppDbContext> contextFactory)
    : IConfigurationStore
{
    public string? GetValue(string key)
    {
        using AppDbContext context = contextFactory.CreateDbContext();
        DbConfiguration? config = context.Configuration.FirstOrDefault(c => c.Key == key);
        return config?.Value;
    }

    public void SetValue(string key, string value)
    {
        using AppDbContext context = contextFactory.CreateDbContext();
        context.SetValue(key, value);
    }

    public async Task SetValueAsync(string key, string value, Guid? modifiedBy = null)
    {
        await using AppDbContext context = await contextFactory.CreateDbContextAsync();
        await context.SetValueAsync(key, value, modifiedBy);
    }

    public bool HasKey(string key)
    {
        using AppDbContext context = contextFactory.CreateDbContext();
        return context.Configuration.Any(c => c.Key == key);
    }
}
