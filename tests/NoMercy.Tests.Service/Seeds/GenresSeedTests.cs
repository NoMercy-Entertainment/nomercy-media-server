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
/// <see cref="GenresSeed.Init"/> guards BOTH its genre fetch and its
/// per-language translation fan-out (a <c>Parallel.ForEachAsync</c> over every
/// non-English language, each doing two more TMDB calls) behind a single
/// "genres already exist" check at the top. A regression that moved the guard
/// past the translations block would turn every boot into dozens of TMDB
/// requests instead of zero.
/// </summary>
[Trait("Category", "Unit")]
public sealed class GenresSeedTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MediaContext> _options;

    public GenresSeedTests()
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
    public async Task Init_GenresAlreadySeeded_SkipsFetchAndTranslationFanOut()
    {
        await using MediaContext seedContext = new(_options);
        seedContext.Genres.Add(new() { Id = 28, Name = "Action" });
        seedContext.Languages.Add(
            new()
            {
                Iso6391 = "nl",
                EnglishName = "Dutch",
                Name = "Nederlands",
            }
        );
        await seedContext.SaveChangesAsync();

        await using MediaContext context = new(_options);

        await GenresSeed.Init(context);

        int genreCount = await context.Genres.CountAsync();
        int translationCount = await context.Translations.CountAsync();
        Assert.Equal(1, genreCount);
        Assert.Equal(0, translationCount);
    }
}
