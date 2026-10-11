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
using Moq;
using NoMercy.Database;
using NoMercy.Database.Models.Common;
using NoMercy.Providers.AniList;
using NoMercy.Providers.AniList.Models;
using NoMercy.Providers.Jikan;
using NoMercy.Providers.Jikan.Models;
using NoMercy.Service.Seeds;
using NoMercy.Tests.Common;

namespace NoMercy.Tests.Service.Seeds;

[Trait("Category", "Unit")]
public sealed class AnimeThemesSeedTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MediaContext> _options;

    public AnimeThemesSeedTests()
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

    private static AniListMediaTag Tag(
        string name,
        string? category,
        string? description = null,
        bool isAdult = false
    ) =>
        new()
        {
            Name = name,
            Category = category,
            Description = description,
            IsAdult = isAdult,
        };

    private static JikanGenre Genre(string name) => new() { Name = name };

    private static Mock<IAniListMetadataProvider> AniList(params AniListMediaTag[]? tags)
    {
        Mock<IAniListMetadataProvider> provider = new();
        provider.Setup(p => p.GetTagCollectionAsync(It.IsAny<bool?>())).ReturnsAsync(tags);
        return provider;
    }

    private static Mock<IJikanMetadataProvider> Jikan(
        JikanGenre[]? themes,
        JikanGenre[]? demographics
    )
    {
        Mock<IJikanMetadataProvider> provider = new();
        provider.Setup(p => p.GetGenresAsync("themes", It.IsAny<bool?>())).ReturnsAsync(themes);
        provider
            .Setup(p => p.GetGenresAsync("demographics", It.IsAny<bool?>()))
            .ReturnsAsync(demographics);
        return provider;
    }

    [Fact]
    public async Task Init_SeedsEveryThemeAndDemographic_FilteringTagsLikeEnrichment()
    {
        Mock<IAniListMetadataProvider> aniList = AniList(
            Tag("Isekai", "Theme-Fantasy", "Another world."),
            Tag("Space", "Setting-Universe"),
            Tag("Ecchi", "Theme-Other", isAdult: true),
            Tag("Male Protagonist", "Cast-Main Cast"),
            Tag("Uncategorized", null),
            Tag("   ", "Theme-Other")
        );
        Mock<IJikanMetadataProvider> jikan = Jikan(
            [Genre("Gore"), Genre("isekai")],
            [Genre("Shounen"), Genre("Seinen")]
        );

        await using MediaContext context = new(_options);
        await AnimeThemesSeed.Init(context, aniList.Object, jikan.Object);

        string[] themes = await context
            .AnimeThemes.Select(t => t.Name)
            .OrderBy(n => n)
            .ToArrayAsync();
        string[] demographics = await context
            .AnimeDemographics.Select(d => d.Name)
            .OrderBy(n => n)
            .ToArrayAsync();
        Assert.Equal(["Gore", "Isekai", "Space"], themes);
        Assert.Equal(["Seinen", "Shounen"], demographics);
        Assert.Equal(
            "Another world.",
            (await context.AnimeThemes.SingleAsync(t => t.Name == "Isekai")).Description
        );
    }

    [Fact]
    public async Task Init_RunTwice_AddsNothingTheSecondTime()
    {
        Mock<IAniListMetadataProvider> aniList = AniList(Tag("Isekai", "Theme-Fantasy", "Desc"));
        Mock<IJikanMetadataProvider> jikan = Jikan([Genre("Gore")], [Genre("Shounen")]);

        await using MediaContext context = new(_options);
        await AnimeThemesSeed.Init(context, aniList.Object, jikan.Object);
        await AnimeThemesSeed.Init(context, aniList.Object, jikan.Object);

        Assert.Equal(2, await context.AnimeThemes.CountAsync());
        Assert.Equal(1, await context.AnimeDemographics.CountAsync());
    }

    [Fact]
    public async Task Init_WhenSomeRowsExist_StillInsertsTheMissingOnes()
    {
        await using (MediaContext seedContext = new(_options))
        {
            seedContext.AnimeThemes.Add(new() { Name = "Gore" });
            await seedContext.SaveChangesAsync();
        }

        Mock<IAniListMetadataProvider> aniList = AniList(Tag("Isekai", "Theme-Fantasy"));
        Mock<IJikanMetadataProvider> jikan = Jikan([Genre("Gore")], []);

        await using MediaContext context = new(_options);
        await AnimeThemesSeed.Init(context, aniList.Object, jikan.Object);

        Assert.Equal(
            ["Gore", "Isekai"],
            await context.AnimeThemes.Select(t => t.Name).OrderBy(n => n).ToArrayAsync()
        );
    }

    [Fact]
    public async Task Init_FillsAnEmptyDescription_AndKeepsAnExistingOne_WithoutDuplicates()
    {
        await using (MediaContext seedContext = new(_options))
        {
            seedContext.AnimeThemes.AddRange(
                new AnimeTheme { Name = "isekai" },
                new AnimeTheme { Name = "Space", Description = "Mine" }
            );
            await seedContext.SaveChangesAsync();
        }

        Mock<IAniListMetadataProvider> aniList = AniList(
            Tag("Isekai", "Theme-Fantasy", "Another world."),
            Tag("Space", "Setting-Universe", "Provider text")
        );
        Mock<IJikanMetadataProvider> jikan = Jikan([], []);

        await using MediaContext context = new(_options);
        await AnimeThemesSeed.Init(context, aniList.Object, jikan.Object);

        List<AnimeTheme> themes = await context.AnimeThemes.OrderBy(t => t.Id).ToListAsync();
        Assert.Equal(2, themes.Count);
        Assert.Equal("Another world.", themes[0].Description);
        Assert.Equal("Mine", themes[1].Description);
    }

    [Fact]
    public async Task Init_BothProvidersOffline_DoesNotThrowAndKeepsExistingRows()
    {
        await using (MediaContext seedContext = new(_options))
        {
            seedContext.AnimeThemes.Add(new() { Name = "Gore" });
            await seedContext.SaveChangesAsync();
        }

        Mock<IAniListMetadataProvider> aniList = new();
        aniList
            .Setup(p => p.GetTagCollectionAsync(It.IsAny<bool?>()))
            .ThrowsAsync(new HttpRequestException("offline"));
        Mock<IJikanMetadataProvider> jikan = new();
        jikan
            .Setup(p => p.GetGenresAsync(It.IsAny<string>(), It.IsAny<bool?>()))
            .ThrowsAsync(new HttpRequestException("offline"));

        await using MediaContext context = new(_options);
        await AnimeThemesSeed.Init(context, aniList.Object, jikan.Object);

        Assert.Equal(1, await context.AnimeThemes.CountAsync());
        Assert.Equal(0, await context.AnimeDemographics.CountAsync());
    }

    [Fact]
    public async Task Init_AniListOfflineAndJikanNull_SeedsWhatTheOtherProviderGave()
    {
        Mock<IAniListMetadataProvider> aniList = AniList(null);
        Mock<IJikanMetadataProvider> jikan = Jikan([Genre("Gore")], null);

        await using MediaContext context = new(_options);
        await AnimeThemesSeed.Init(context, aniList.Object, jikan.Object);

        Assert.Equal(["Gore"], await context.AnimeThemes.Select(t => t.Name).ToArrayAsync());
        Assert.Equal(0, await context.AnimeDemographics.CountAsync());
    }

    [Fact]
    public void DatabaseSeederRun_RegistersTheAnimeThemesSeed()
    {
        string source = File.ReadAllText(
            RepoPaths.At("src/NoMercy.Service/Seeds/DatabaseSeeder.cs")
        );

        Assert.Contains("AnimeThemesSeed.Init(mediaDbContext)", source);
    }
}
