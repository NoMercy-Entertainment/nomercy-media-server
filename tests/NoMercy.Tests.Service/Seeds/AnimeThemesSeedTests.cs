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

using System.Globalization;
using System.Net.Sockets;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using NoMercy.Database;
using NoMercy.Database.Models.Common;
using NoMercy.NmSystem.Dto;
using NoMercy.NmSystem.SystemCalls;
using NoMercy.Providers.AniList;
using NoMercy.Providers.AniList.Models;
using NoMercy.Providers.Jikan;
using NoMercy.Providers.Jikan.Models;
using NoMercy.Service.Seeds;
using NoMercy.Tests.Common;
using Serilog.Events;

namespace NoMercy.Tests.Service.Seeds;

[Trait("Category", "Unit")]
public sealed class AnimeThemesSeedTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MediaContext> _options;
    private readonly SqliteConnection _appConnection;
    private readonly DbContextOptions<AppDbContext> _appOptions;
    private readonly List<LogEntry> _logs = [];

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

        _appConnection = new("DataSource=:memory:");
        _appConnection.Open();
        _appOptions = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_appConnection).Options;
        using AppDbContext appCtx = new(_appOptions);
        appCtx.Database.EnsureCreated();

        Logger.LogEmitted += OnLogEmitted;
    }

    public void Dispose()
    {
        Logger.LogEmitted -= OnLogEmitted;
        _connection.Dispose();
        _appConnection.Dispose();
    }

    private void OnLogEmitted(LogEntry entry)
    {
        lock (_logs)
            _logs.Add(entry);
    }

    private AppDbContext NewAppContext() => new(_appOptions);

    private List<LogEntry> SeedLogs(LogEventLevel level)
    {
        lock (_logs)
            return _logs
                .Where(entry =>
                    entry.LogLevel == level && entry.Message.Contains("Anime themes seed")
                )
                .ToList();
    }

    private async Task StoreSeedState(string key, DateTime when)
    {
        await using AppDbContext appContext = NewAppContext();
        appContext.Configuration.Add(
            new() { Key = key, Value = when.ToString("O", CultureInfo.InvariantCulture) }
        );
        await appContext.SaveChangesAsync();
    }

    private async Task<string?> ReadSeedState(string key)
    {
        await using AppDbContext appContext = NewAppContext();
        return await appContext
            .Configuration.AsNoTracking()
            .Where(c => c.Key == key)
            .Select(c => c.Value)
            .SingleOrDefaultAsync();
    }

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
        await AnimeThemesSeed.Init(context, aniList.Object, jikan.Object, NewAppContext());

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
        await AnimeThemesSeed.Init(context, aniList.Object, jikan.Object, NewAppContext());
        await AnimeThemesSeed.Init(context, aniList.Object, jikan.Object, NewAppContext());

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
        await AnimeThemesSeed.Init(context, aniList.Object, jikan.Object, NewAppContext());

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
        await AnimeThemesSeed.Init(context, aniList.Object, jikan.Object, NewAppContext());

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
        await AnimeThemesSeed.Init(context, aniList.Object, jikan.Object, NewAppContext());

        Assert.Equal(1, await context.AnimeThemes.CountAsync());
        Assert.Equal(0, await context.AnimeDemographics.CountAsync());
    }

    [Fact]
    public async Task Init_AniListOfflineAndJikanNull_SeedsWhatTheOtherProviderGave()
    {
        Mock<IAniListMetadataProvider> aniList = AniList(null);
        Mock<IJikanMetadataProvider> jikan = Jikan([Genre("Gore")], null);

        await using MediaContext context = new(_options);
        await AnimeThemesSeed.Init(context, aniList.Object, jikan.Object, NewAppContext());

        Assert.Equal(["Gore"], await context.AnimeThemes.Select(t => t.Name).ToArrayAsync());
        Assert.Equal(0, await context.AnimeDemographics.CountAsync());
    }

    [Fact]
    public async Task Init_ThemesFetchedRecently_DoesNotCallEitherProvider()
    {
        await StoreSeedState(AnimeThemesSeed.FetchedAtKey, DateTime.UtcNow.AddDays(-1));
        Mock<IAniListMetadataProvider> aniList = AniList(Tag("Isekai", "Theme-Fantasy"));
        Mock<IJikanMetadataProvider> jikan = Jikan([Genre("Gore")], [Genre("Shounen")]);

        await using MediaContext context = new(_options);
        await AnimeThemesSeed.Init(context, aniList.Object, jikan.Object, NewAppContext());

        aniList.Verify(p => p.GetTagCollectionAsync(It.IsAny<bool?>()), Times.Never);
        jikan.Verify(p => p.GetGenresAsync(It.IsAny<string>(), It.IsAny<bool?>()), Times.Never);
    }

    [Fact]
    public async Task Init_ThemesOlderThanTheRefreshWindow_FetchesAgainAndRecordsIt()
    {
        await StoreSeedState(AnimeThemesSeed.FetchedAtKey, DateTime.UtcNow.AddDays(-40));
        Mock<IAniListMetadataProvider> aniList = AniList(Tag("Isekai", "Theme-Fantasy"));
        Mock<IJikanMetadataProvider> jikan = Jikan([Genre("Gore")], [Genre("Shounen")]);

        await using MediaContext context = new(_options);
        await AnimeThemesSeed.Init(context, aniList.Object, jikan.Object, NewAppContext());

        jikan.Verify(p => p.GetGenresAsync("themes", It.IsAny<bool?>()), Times.Once);
        string? stored = await ReadSeedState(AnimeThemesSeed.FetchedAtKey);
        Assert.NotNull(stored);
        DateTime fetchedAt = DateTime.Parse(
            stored,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind
        );
        Assert.True(DateTime.UtcNow - fetchedAt < TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task Init_AfterASuccessfulFetch_TheNextStartSkipsTheProviders()
    {
        Mock<IAniListMetadataProvider> aniList = AniList(Tag("Isekai", "Theme-Fantasy"));
        Mock<IJikanMetadataProvider> jikan = Jikan([Genre("Gore")], [Genre("Shounen")]);

        await using MediaContext firstStart = new(_options);
        await AnimeThemesSeed.Init(firstStart, aniList.Object, jikan.Object, NewAppContext());
        await using MediaContext secondStart = new(_options);
        await AnimeThemesSeed.Init(secondStart, aniList.Object, jikan.Object, NewAppContext());

        aniList.Verify(p => p.GetTagCollectionAsync(It.IsAny<bool?>()), Times.Once);
        jikan.Verify(p => p.GetGenresAsync("themes", It.IsAny<bool?>()), Times.Once);
        jikan.Verify(p => p.GetGenresAsync("demographics", It.IsAny<bool?>()), Times.Once);
    }

    public static TheoryData<Exception> UnreachableJikanFailures =>
        new()
        {
            new TaskCanceledException(
                "The request was canceled due to the configured HttpClient.Timeout of 15 seconds elapsing.",
                new TimeoutException()
            ),
            new HttpRequestException(
                "A connection attempt failed because the connected party did not respond (api.jikan.moe:443)",
                new SocketException((int)SocketError.TimedOut)
            ),
        };

    [Theory]
    [MemberData(nameof(UnreachableJikanFailures))]
    public async Task Init_JikanUnreachable_LogsOnePlainWarning_AndStillSeedsAniListThemes(
        Exception failure
    )
    {
        Mock<IAniListMetadataProvider> aniList = AniList(Tag("Isekai", "Theme-Fantasy"));
        Mock<IJikanMetadataProvider> jikan = new();
        jikan
            .Setup(p => p.GetGenresAsync(It.IsAny<string>(), It.IsAny<bool?>()))
            .ThrowsAsync(failure);

        await using MediaContext context = new(_options);
        Exception? thrown = await Record.ExceptionAsync(() =>
            AnimeThemesSeed.Init(context, aniList.Object, jikan.Object, NewAppContext())
        );

        Assert.Null(thrown);
        LogEntry warning = Assert.Single(SeedLogs(LogEventLevel.Warning));
        Assert.Contains("Jikan", warning.Message);
        Assert.DoesNotContain(failure.Message, warning.Message);
        Assert.Empty(SeedLogs(LogEventLevel.Error));
        Assert.Equal(["Isekai"], await context.AnimeThemes.Select(t => t.Name).ToArrayAsync());
    }

    [Fact]
    public async Task Init_AfterAFailedAttempt_DoesNotAskAgainOnTheNextStart()
    {
        Mock<IAniListMetadataProvider> aniList = AniList(Tag("Isekai", "Theme-Fantasy"));
        Mock<IJikanMetadataProvider> jikan = new();
        jikan
            .Setup(p => p.GetGenresAsync(It.IsAny<string>(), It.IsAny<bool?>()))
            .ThrowsAsync(new TaskCanceledException("timeout", new TimeoutException()));

        await using MediaContext firstStart = new(_options);
        await AnimeThemesSeed.Init(firstStart, aniList.Object, jikan.Object, NewAppContext());
        await using MediaContext secondStart = new(_options);
        await AnimeThemesSeed.Init(secondStart, aniList.Object, jikan.Object, NewAppContext());

        jikan.Verify(p => p.GetGenresAsync(It.IsAny<string>(), It.IsAny<bool?>()), Times.Once);
        Assert.Single(SeedLogs(LogEventLevel.Warning));
        Assert.Null(await ReadSeedState(AnimeThemesSeed.FetchedAtKey));
    }

    [Fact]
    public async Task Init_FailedAttemptOlderThanTheCooldown_TriesAgain()
    {
        await StoreSeedState(AnimeThemesSeed.FailedAtKey, DateTime.UtcNow.AddHours(-7));
        Mock<IAniListMetadataProvider> aniList = AniList(Tag("Isekai", "Theme-Fantasy"));
        Mock<IJikanMetadataProvider> jikan = Jikan([Genre("Gore")], [Genre("Shounen")]);

        await using MediaContext context = new(_options);
        await AnimeThemesSeed.Init(context, aniList.Object, jikan.Object, NewAppContext());

        jikan.Verify(p => p.GetGenresAsync("themes", It.IsAny<bool?>()), Times.Once);
        Assert.NotNull(await ReadSeedState(AnimeThemesSeed.FetchedAtKey));
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
