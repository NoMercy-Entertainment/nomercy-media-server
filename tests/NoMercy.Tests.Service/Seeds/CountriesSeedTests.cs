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

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NoMercy.Database;
using NoMercy.Service.Seeds;

namespace NoMercy.Tests.Service.Seeds;

/// <summary>
/// <see cref="CountriesSeed.Init"/> must skip the TMDB fetch once the Countries
/// table has any rows — otherwise every boot re-fetches the full country list
/// from the network for data that never changes.
/// </summary>
[Trait("Category", "Unit")]
public sealed class CountriesSeedTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MediaContext> _options;

    public CountriesSeedTests()
    {
        _connection = new("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<MediaContext>()
            .UseSqlite(
                _connection,
                o => o.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery)
            )
            .Options;

        using MediaContext ctx = new(_options);
        ctx.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task Init_CountriesAlreadySeeded_ReturnsWithoutCallingNetwork()
    {
        await using MediaContext seedContext = new(_options);
        seedContext.Countries.Add(
            new()
            {
                Iso31661 = "US",
                EnglishName = "United States of America",
                NativeName = "United States of America",
            }
        );
        await seedContext.SaveChangesAsync();

        await using MediaContext context = new(_options);

        await CountriesSeed.Init(context);

        int count = await context.Countries.CountAsync();
        Assert.Equal(1, count);
    }
}
