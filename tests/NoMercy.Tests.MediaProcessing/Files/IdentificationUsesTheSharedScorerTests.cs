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
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MovieFileLibrary;
using NoMercy.Database;
using NoMercy.MediaProcessing.Files;
using NoMercy.MediaProcessing.Files.Parsing;
using NoMercy.MediaProcessing.Files.Parsing.Adapters;
using NoMercy.NmSystem.Domain;
using NoMercy.Providers.Helpers;
using NoMercy.Providers.TMDB.Models.Shared;
using NoMercy.Tests.Common.Providers;

namespace NoMercy.Tests.MediaProcessing.Files;

/// <summary>
/// Add content must pick the best scored TMDB candidate, as the folder scan and
/// the watcher do, and refuse a hit that does not look like the file name.
/// </summary>
[Collection("HttpClientProvider")]
public class IdentificationUsesTheSharedScorerTests : ProviderHttpHarness
{
    private const int WantedId = 771101;
    private const int DecoyId = 771102;
    private const string File = "Harbor.Lights.2026.2160p.UHD.BluRay.REMUX-GRP.mkv";

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MediaContext> _options;

    public IdentificationUsesTheSharedScorerTests()
        : base("TMDB")
    {
        _connection = new("Data Source=:memory:");
        _connection.Open();

        using (SqliteCommand fkOff = _connection.CreateCommand())
        {
            fkOff.CommandText = "PRAGMA foreign_keys = OFF;";
            fkOff.ExecuteNonQuery();
        }

        _options = new DbContextOptionsBuilder<MediaContext>().UseSqlite(_connection).Options;
        using MediaContext ctx = new(_options);
        ctx.Database.EnsureCreated();
    }

    public override void Dispose()
    {
        _connection.Dispose();
        base.Dispose();
        HttpClientProvider.Initialize(new TmdbMockHttpClientFactory());
        GC.SuppressFinalize(this);
    }

    private void ScriptSearch(string firstTitle, string secondTitle)
    {
        Handler.WhenGet(
            "/search/movie",
            MockResponse.Json(
                HttpStatusCode.OK,
                $$"""
                {"page":1,"total_pages":1,"total_results":2,"results":[
                  {"id":{{DecoyId}},"title":"{{firstTitle}}","release_date":"2020-05-01"},
                  {"id":{{WantedId}},"title":"{{secondTitle}}","release_date":"2026-05-01"}
                ]}
                """
            )
        );
        foreach ((int id, string title) in new[] { (DecoyId, firstTitle), (WantedId, secondTitle) })
        {
            Handler.WhenGet(
                $"/movie/{id}",
                MockResponse.Json(
                    HttpStatusCode.OK,
                    $$"""
                    {"id":{{id}},"title":"{{title}}","overview":"","poster_path":"/p.jpg","imdb_id":"tt0000002","release_date":"2026-05-01"}
                    """
                )
            );
        }
    }

    private async Task<(MovieOrEpisode match, string? imdbId)?> Identify()
    {
        FilenameResolver resolver = new(
            new FilenameParserPipeline(new IFilenameParseAdapter[] { new MovieDetectorAdapter() })
        );
        ResolvedName resolved = resolver.Resolve(
            File,
            "/downloads",
            $"/downloads/{File}",
            MediaTypes.MovieMediaType
        );

        await using MediaContext context = new(_options);
        MediaIdentificationService service = new(
            context,
            new ServiceCollection()
                .BuildServiceProvider()
                .GetRequiredService<IServiceScopeFactory>()
        );

        return await service.IdentifyAsync(
            resolved.Parsed,
            MediaTypes.MovieMediaType,
            duration: null,
            resolved.OverrideTmdbId,
            resolved.SeasonExplicit,
            resolved.AirDate
        );
    }

    [Fact]
    public async Task The_best_scored_candidate_wins_over_the_first_hit()
    {
        ScriptSearch("Harbor Lights Documentary Cut Extras", "Harbor Lights");

        (MovieOrEpisode match, string? imdbId)? result = await Identify();

        result.Should().NotBeNull();
        ((int)result!.Value.match.Id).Should().Be(WantedId);
    }

    [Fact]
    public async Task A_best_candidate_that_does_not_look_like_the_file_is_refused()
    {
        ScriptSearch("Zebra Quarry", "Mountain Pass");

        (await Identify()).Should().BeNull();
    }
}
