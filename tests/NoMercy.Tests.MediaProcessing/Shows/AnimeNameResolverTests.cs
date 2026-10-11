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
using NoMercy.Database.Models.Common;
using NoMercy.MediaProcessing.Movies;
using NoMercy.MediaProcessing.Shows;

namespace NoMercy.Tests.MediaProcessing.Shows;

/// <summary>
/// A seeded "Isekai" and an enriched "isekai " must be one row, or the theme list
/// shows the same theme twice. Both repositories resolve names the same way.
/// </summary>
[Trait("Category", "Unit")]
public sealed class AnimeNameResolverTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MediaContext> _options;

    public AnimeNameResolverTests()
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
    public async Task ShowRepository_FillThemeDescription_FillsEmptyAndKeepsExisting()
    {
        await using MediaContext context = new(_options);
        ShowRepository repository = new(context);
        int empty = await repository.ResolveAnimeThemeIdAsync("Isekai");
        context.AnimeThemes.Add(new() { Name = "Space", Description = "Mine" });
        await context.SaveChangesAsync();
        int kept = (await context.AnimeThemes.SingleAsync(t => t.Name == "Space")).Id;

        await repository.FillAnimeThemeDescriptionAsync(empty, "Another world.");
        await repository.FillAnimeThemeDescriptionAsync(kept, "Provider text");

        context.ChangeTracker.Clear();
        Assert.Equal(
            "Another world.",
            (await context.AnimeThemes.SingleAsync(t => t.Id == empty)).Description
        );
        Assert.Equal(
            "Mine",
            (await context.AnimeThemes.SingleAsync(t => t.Id == kept)).Description
        );
    }

    [Fact]
    public async Task MovieRepository_FillThemeDescription_FillsEmptyAndKeepsExisting()
    {
        await using MediaContext context = new(_options);
        MovieRepository repository = new(context);
        int empty = await repository.ResolveAnimeThemeIdAsync("Isekai");
        context.AnimeThemes.Add(new() { Name = "Space", Description = "Mine" });
        await context.SaveChangesAsync();
        int kept = (await context.AnimeThemes.SingleAsync(t => t.Name == "Space")).Id;

        await repository.FillAnimeThemeDescriptionAsync(empty, "Another world.");
        await repository.FillAnimeThemeDescriptionAsync(kept, "Provider text");

        context.ChangeTracker.Clear();
        Assert.Equal(
            "Another world.",
            (await context.AnimeThemes.SingleAsync(t => t.Id == empty)).Description
        );
        Assert.Equal(
            "Mine",
            (await context.AnimeThemes.SingleAsync(t => t.Id == kept)).Description
        );
    }

    [Fact]
    public async Task ShowRepository_ThemeNamesDifferingInCaseAndWhitespace_ResolveToOneRow()
    {
        await using MediaContext context = new(_options);
        ShowRepository repository = new(context);

        int first = await repository.ResolveAnimeThemeIdAsync("Isekai");
        int second = await repository.ResolveAnimeThemeIdAsync("  isekai ");

        Assert.Equal(first, second);
        Assert.Equal(1, await context.AnimeThemes.CountAsync());
        Assert.Equal("Isekai", (await context.AnimeThemes.SingleAsync()).Name);
    }

    [Fact]
    public async Task MovieRepository_ThemeNamesDifferingInCaseAndWhitespace_ResolveToOneRow()
    {
        await using MediaContext context = new(_options);
        MovieRepository repository = new(context);

        int first = await repository.ResolveAnimeThemeIdAsync("Isekai");
        int second = await repository.ResolveAnimeThemeIdAsync("  isekai ");

        Assert.Equal(first, second);
        Assert.Equal(1, await context.AnimeThemes.CountAsync());
    }

    [Fact]
    public async Task ShowRepository_NewThemeWithPaddedName_StoresTheTrimmedName()
    {
        await using MediaContext context = new(_options);
        ShowRepository repository = new(context);

        await repository.ResolveAnimeThemeIdAsync("  Time Loop ");

        Assert.Equal("Time Loop", (await context.AnimeThemes.SingleAsync()).Name);
    }

    [Fact]
    public async Task ShowRepository_DemographicNamesDifferingInCaseAndWhitespace_ResolveToOneRow()
    {
        await using MediaContext context = new(_options);
        ShowRepository repository = new(context);

        int first = await repository.ResolveAnimeDemographicIdAsync("Shounen");
        int second = await repository.ResolveAnimeDemographicIdAsync(" shounen");

        Assert.Equal(first, second);
        Assert.Equal(1, await context.AnimeDemographics.CountAsync());
    }

    [Fact]
    public async Task MovieRepository_DemographicNamesDifferingInCaseAndWhitespace_ResolveToOneRow()
    {
        await using MediaContext context = new(_options);
        MovieRepository repository = new(context);

        int first = await repository.ResolveAnimeDemographicIdAsync("Shounen");
        int second = await repository.ResolveAnimeDemographicIdAsync(" SHOUNEN ");

        Assert.Equal(first, second);
        Assert.Equal(1, await context.AnimeDemographics.CountAsync());
    }

    [Fact]
    public async Task ShowRepository_ExistingSeededRowWithPadding_IsFoundByTrimmedName()
    {
        await using MediaContext seed = new(_options);
        AnimeTheme seeded = new() { Name = "Mecha" };
        seed.AnimeThemes.Add(seeded);
        await seed.SaveChangesAsync();

        await using MediaContext context = new(_options);
        ShowRepository repository = new(context);

        int id = await repository.ResolveAnimeThemeIdAsync("mecha");

        Assert.Equal(seeded.Id, id);
    }
}
