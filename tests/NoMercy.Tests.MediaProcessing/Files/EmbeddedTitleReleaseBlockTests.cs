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
using Microsoft.Extensions.DependencyInjection;
using MovieFileLibrary;
using NoMercy.Database;
using NoMercy.MediaProcessing.Files;
using NoMercy.NmSystem.Domain;
using NoMercy.Providers.Helpers;
using NoMercy.Providers.TMDB.Models.Shared;
using NoMercy.Tests.Common.Providers;

namespace NoMercy.Tests.MediaProcessing.Files;

/// <summary>
/// A fansub batch writes a bracket release block right after the episode marker:
/// "S01E01 (BD 1080p HEVC Opus) [Dual Audio] [64178EF5]". That block is not an
/// episode title, so it must not rule out the held episode that the number names.
/// </summary>
[Collection("HttpClientProvider")]
public class EmbeddedTitleReleaseBlockTests : ProviderHttpHarness
{
    private const int ShowId = 771003;
    private const string ShowName = "Moonlit Harbor Tales";

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MediaContext> _options;

    public EmbeddedTitleReleaseBlockTests()
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

        ctx.Tvs.Add(new() { Id = ShowId, Title = ShowName });
        ctx.Seasons.Add(
            new()
            {
                Id = 1,
                TvId = ShowId,
                SeasonNumber = 1,
            }
        );
        string[] titles = ["The Lantern Pier", "A Quiet Tide", "Salt and Thread"];
        for (int index = 0; index < titles.Length; index++)
            ctx.Episodes.Add(
                new()
                {
                    Id = index + 1,
                    TvId = ShowId,
                    SeasonId = 1,
                    SeasonNumber = 1,
                    EpisodeNumber = index + 1,
                    Title = titles[index],
                }
            );
        ctx.SaveChanges();

        Handler.WhenGet(
            "/search/tv",
            MockResponse.Json(
                HttpStatusCode.OK,
                $$"""
                {"page":1,"total_pages":1,"total_results":1,"results":[
                  {"id":{{ShowId}},"name":"{{ShowName}}","first_air_date":"2020-01-01"}
                ]}
                """
            )
        );
        Handler.WhenGet(
            $"/tv/{ShowId}/episode_groups",
            MockResponse.Json(HttpStatusCode.OK, """{"id":1,"results":[]}""")
        );
        foreach (int number in new[] { 1, 2, 3 })
            Handler.WhenGet(
                $"/tv/{ShowId}/season/1/episode/{number}",
                MockResponse.Status(HttpStatusCode.NotFound)
            );
    }

    public override void Dispose()
    {
        _connection.Dispose();
        base.Dispose();
        HttpClientProvider.Initialize(new TmdbMockHttpClientFactory());
        GC.SuppressFinalize(this);
    }

    private async Task<(MovieOrEpisode match, string? imdbId)?> Identify(
        string fileName,
        int episode
    )
    {
        await using MediaContext context = new(_options);
        ServiceCollection services = new();
        MediaIdentificationService service = new(
            context,
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>()
        );

        MovieFile parsed = new($"/downloads/{fileName}.mkv")
        {
            Title = ShowName,
            Season = 1,
            Episode = episode,
            IsSeries = true,
            IsSuccess = true,
        };

        return await service.IdentifyAsync(
            parsed,
            MediaTypes.TvMediaType,
            duration: null,
            overrideTmdbId: null,
            seasonExplicit: true
        );
    }

    [Theory]
    [InlineData(
        "[Starbez] Moonlit Harbor Tales - S01E01 (BD 1080p HEVC Opus) [Dual Audio] [64178EF5]",
        1
    )]
    [InlineData("[SubsPlease] Moonlit Harbor Tales - S01E02 (1080p) [ABCD1234]", 2)]
    [InlineData("[Group] Moonlit Harbor Tales - S01E03 [WEB 1080p] [ABCD1234]", 3)]
    [InlineData("Moonlit Harbor Tales.S01E02.WEB.x264-GRP", 2)]
    [InlineData("Moonlit Harbor Tales - S01E03 - Salt and Thread (1080p)", 3)]
    public async Task A_release_block_after_the_marker_does_not_reject_the_held_episode(
        string fileName,
        int episode
    )
    {
        (MovieOrEpisode match, string? imdbId)? result = await Identify(fileName, episode);

        result.Should().NotBeNull();
        result!.Value.match.SeasonNumber.Should().Be(1);
        result.Value.match.EpisodeNumber.Should().Be(episode);
    }

    [Fact]
    public async Task A_real_title_that_names_other_content_still_rejects_the_number()
    {
        (MovieOrEpisode match, string? imdbId)? result = await Identify(
            "Moonlit Harbor Tales - S01E01 - Midsummer Night Dream (1080p)",
            1
        );

        result.Should().BeNull();
    }
}
