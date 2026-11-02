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
/// The Add content dialog lists a movie folder before the owner has added the
/// movie, so identification must answer from TMDB and write nothing.
/// </summary>
[Collection("HttpClientProvider")]
public class MovieNotYetInLibraryIsIdentifiedTests : ProviderHttpHarness
{
    private const int MovieId = 771004;
    private const string File = "Harbor.Lights.2026.2160p.UHD.BluRay.REMUX-GRP.mkv";

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MediaContext> _options;

    public MovieNotYetInLibraryIsIdentifiedTests()
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

    private void ScriptSearch()
    {
        Handler.WhenGet(
            "/search/movie",
            MockResponse.Json(
                HttpStatusCode.OK,
                $$"""
                {"page":1,"total_pages":1,"total_results":1,"results":[
                  {"id":{{MovieId}},"title":"Harbor Lights","release_date":"2026-05-01"}
                ]}
                """
            )
        );
    }

    private void ScriptDetails()
    {
        Handler.WhenGet(
            $"/movie/{MovieId}",
            MockResponse.Json(
                HttpStatusCode.OK,
                $$"""
                {"id":{{MovieId}},"title":"Harbor Lights","overview":"A lighthouse story.","poster_path":"/poster.jpg","imdb_id":"tt0000001","release_date":"2026-05-01"}
                """
            )
        );
    }

    private async Task<(MovieOrEpisode match, string? imdbId)?> Identify(MediaContext context)
    {
        FilenameResolver resolver = new(
            new FilenameParserPipeline(
                new IFilenameParseAdapter[] { new MovieDetectorAdapter() }
            )
        );
        ResolvedName resolved = resolver.Resolve(
            File,
            "/downloads",
            $"/downloads/{File}",
            MediaTypes.MovieMediaType
        );

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
    public async Task A_movie_the_server_does_not_hold_is_identified_and_nothing_is_written()
    {
        ScriptSearch();
        ScriptDetails();

        await using MediaContext context = new(_options);
        (MovieOrEpisode match, string? imdbId)? result = await Identify(context);

        result.Should().NotBeNull();
        ((int)result!.Value.match.Id).Should().Be(MovieId);
        result.Value.match.Title.Should().Be("Harbor Lights");
        result.Value.match.Still.Should().Be("/poster.jpg");
        result.Value.imdbId.Should().Be("tt0000001");
        (await context.Movies.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task A_movie_the_server_holds_still_matches_from_its_row()
    {
        ScriptSearch();
        await using (MediaContext seed = new(_options))
        {
            seed.Movies.Add(
                new()
                {
                    Id = MovieId,
                    Title = "Held Title",
                    Poster = "/held.jpg",
                }
            );
            await seed.SaveChangesAsync();
        }

        await using MediaContext context = new(_options);
        (MovieOrEpisode match, string? imdbId)? result = await Identify(context);

        result.Should().NotBeNull();
        result!.Value.match.Title.Should().Be("Held Title");
        result.Value.match.Still.Should().Be("/held.jpg");
    }

    [Fact]
    public async Task Missing_tmdb_details_return_no_match()
    {
        ScriptSearch();
        Handler.WhenGet($"/movie/{MovieId}", MockResponse.Json(HttpStatusCode.NotFound, "{}"));

        await using MediaContext context = new(_options);
        (await Identify(context)).Should().BeNull();
    }
}
