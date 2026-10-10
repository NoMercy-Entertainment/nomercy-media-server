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
/// <see cref="LanguagesSeed.Init"/> must skip the TMDB fetch once the Languages
/// table has any rows. <see cref="GenresSeed.Init"/> reads this same table to
/// decide which translations to fetch, so a language seed that re-runs
/// needlessly on every boot would also multiply GenresSeed's per-language fan-out.
/// </summary>
[Trait("Category", "Unit")]
public sealed class LanguagesSeedTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MediaContext> _options;

    public LanguagesSeedTests()
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
    public async Task Init_LanguagesAlreadySeeded_ReturnsWithoutCallingNetwork()
    {
        await using MediaContext seedContext = new(_options);
        seedContext.Languages.Add(
            new()
            {
                Iso6391 = "en",
                EnglishName = "English",
                Name = "English",
            }
        );
        await seedContext.SaveChangesAsync();

        await using MediaContext context = new(_options);

        await LanguagesSeed.Init(context);

        int count = await context.Languages.CountAsync();
        Assert.Equal(1, count);
    }
}
