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

using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NoMercy.Database;
using NoMercy.Database.Models.Media;
using NoMercy.NmSystem.Configuration;
using NoMercy.Providers.Helpers;
using NoMercy.Service.Seeds;
using NoMercy.Tests.Common.Providers;

namespace NoMercy.Tests.Service.Seeds;

[Trait("Category", "Unit")]
[Collection("HttpClientProvider")]
public sealed class GenresSeedTests : ProviderHttpHarness
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MediaContext> _options;
    private readonly SqliteConnection _appConnection;
    private readonly DbContextOptions<AppDbContext> _appOptions;

    public GenresSeedTests()
        : base(HttpClientNames.Tmdb)
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
    }

    public override void Dispose()
    {
        _connection.Dispose();
        _appConnection.Dispose();
        base.Dispose();
    }

    [Fact]
    public async Task Init_GenresAndTranslationsAlreadySeeded_SkipsFetchAndTranslationFanOut()
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
        seedContext.Translations.Add(
            new Translation
            {
                GenreId = 28,
                Iso6391 = "nl",
                Name = "Actie",
            }
        );
        await seedContext.SaveChangesAsync();

        await using MediaContext context = new(_options);

        await GenresSeed.Init(context, new AppDbContext(_appOptions));

        int genreCount = await context.Genres.CountAsync();
        int translationCount = await context.Translations.CountAsync();
        Assert.Equal(1, genreCount);
        Assert.Equal(1, translationCount);
        Assert.Empty(Handler.Requests);
    }

    [Fact]
    public async Task Init_TranslationsFailThenRecover_RetriesWithoutRefetchingGenres()
    {
        await using (MediaContext seedContext = new(_options))
        {
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
        }

        Handler.WhenGet(
            "genre/movie/list",
            MockResponse.Status(HttpStatusCode.NotFound),
            MockResponse.Json(HttpStatusCode.OK, "{\"genres\":[{\"id\":28,\"name\":\"Actie\"}]")
        );
        Handler.WhenGet(
            "genre/tv/list",
            MockResponse.Status(HttpStatusCode.NotFound),
            MockResponse.Json(HttpStatusCode.OK, "{\"genres\":[]}")
        );

        await using (MediaContext firstContext = new(_options))
        {
            await GenresSeed.Init(firstContext, new AppDbContext(_appOptions));
            Assert.Equal(0, await firstContext.Translations.CountAsync(t => t.GenreId != null));
        }

        await using MediaContext secondContext = new(_options);
        await GenresSeed.Init(secondContext, new AppDbContext(_appOptions));

        Translation translation = await secondContext.Translations.SingleAsync(t =>
            t.GenreId == 28
        );
        Assert.Equal("Actie", translation.Name);
        Assert.Equal("nl", translation.Iso6391);
        Assert.Equal(2, Handler.RequestCountFor("genre/movie/list"));
        Assert.Equal(2, Handler.RequestCountFor("genre/tv/list"));
    }

    [Fact]
    public async Task Init_PartialTranslations_RetriesMissingGenre()
    {
        await using (MediaContext seedContext = new(_options))
        {
            seedContext.Genres.AddRange(
                new() { Id = 28, Name = "Action" },
                new() { Id = 18, Name = "Drama" }
            );
            seedContext.Languages.Add(
                new()
                {
                    Iso6391 = "nl",
                    EnglishName = "Dutch",
                    Name = "Nederlands",
                }
            );
            seedContext.Translations.Add(
                new()
                {
                    GenreId = 28,
                    Iso6391 = "nl",
                    Name = "Actie",
                }
            );
            await seedContext.SaveChangesAsync();
        }

        Handler.WhenGet(
            "genre/movie/list",
            MockResponse.Json(
                HttpStatusCode.OK,
                "{\"genres\":[{\"id\":28,\"name\":\"Actie\"},{\"id\":18,\"name\":\"Drama\"}]"
            )
        );
        Handler.WhenGet("genre/tv/list", MockResponse.Json(HttpStatusCode.OK, "{\"genres\":[]}"));

        await using MediaContext context = new(_options);
        await GenresSeed.Init(context, new AppDbContext(_appOptions));

        Assert.Equal(2, await context.Translations.CountAsync(t => t.GenreId != null));
        Assert.Equal(2, await context.Genres.CountAsync());
        Assert.Single(Handler.Requests, r => r.Path.Contains("genre/movie/list"));
    }

    [Fact]
    public async Task Init_LanguageTmdbNeverTranslates_IsNotRefetchedOnNextBoot()
    {
        await using (MediaContext seedContext = new(_options))
        {
            seedContext.Genres.Add(new() { Id = 28, Name = "Action" });
            seedContext.Languages.Add(
                new()
                {
                    Iso6391 = "xx",
                    EnglishName = "Untranslated",
                    Name = "Untranslated",
                }
            );
            await seedContext.SaveChangesAsync();
        }

        Handler.WhenGet("genre/movie/list", MockResponse.Json(HttpStatusCode.OK, "{\"genres\":[]}"));
        Handler.WhenGet("genre/tv/list", MockResponse.Json(HttpStatusCode.OK, "{\"genres\":[]}"));

        await using (MediaContext firstContext = new(_options))
            await GenresSeed.Init(firstContext, new AppDbContext(_appOptions));

        await using (MediaContext secondContext = new(_options))
            await GenresSeed.Init(secondContext, new AppDbContext(_appOptions));

        Assert.Equal(1, Handler.RequestCountFor("genre/movie/list"));
        Assert.Equal(1, Handler.RequestCountFor("genre/tv/list"));
    }
}
