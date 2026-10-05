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
/// An absolute episode number means whatever the provider's absolute ordering says it
/// means, so that ordering has to be consulted before anything is inferred locally.
///
/// The local fallback indexes the show's own episodes as one flat run, which returns a row
/// for any number inside its range. That always looks like a match, so if it runs first the
/// group lookup never happens and a wrong episode is imported silently. These tests pin the
/// order by making the two answers disagree.
/// </summary>
[Collection("HttpClientProvider")]
public class AbsoluteEpisodeResolutionOrderTests : ProviderHttpHarness
{
    private const int ShowId = 771001;
    private const string ShowName = "Ordering Probe";

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MediaContext> _options;

    public AbsoluteEpisodeResolutionOrderTests()
        : base("TMDB", "TVDB", "TvdbLogin")
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

        // The harness resets the process-wide HTTP provider on the way out, but this assembly
        // installs its TMDB mock exactly once for every test in it. Without putting that back,
        // every test running after this class loses its HTTP factory and its provider lookups
        // silently stop happening.
        HttpClientProvider.Initialize(new TmdbMockHttpClientFactory());

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Season one is deliberately missing an episode, which is ordinary for a library that
    /// has not finished scanning. That gap shifts every later entry in the flat run, so the
    /// two strategies land on different episodes for the same absolute number and the test
    /// can tell which one answered.
    /// </summary>
    private void SeedShowWithAGapInSeasonOne()
    {
        using MediaContext ctx = new(_options);

        ctx.Tvs.Add(new() { Id = ShowId, Title = ShowName });
        ctx.Seasons.Add(
            new()
            {
                Id = 1,
                TvId = ShowId,
                SeasonNumber = 1,
            }
        );
        ctx.Seasons.Add(
            new()
            {
                Id = 2,
                TvId = ShowId,
                SeasonNumber = 2,
            }
        );

        int episodeId = 1;
        foreach (int number in Enumerable.Range(1, 12).Where(number => number != 5))
            ctx.Episodes.Add(
                new()
                {
                    Id = episodeId++,
                    TvId = ShowId,
                    SeasonId = 1,
                    SeasonNumber = 1,
                    EpisodeNumber = number,
                    Title = $"S01E{number:D2}",
                }
            );

        foreach (int number in Enumerable.Range(1, 12))
            ctx.Episodes.Add(
                new()
                {
                    Id = 100 + number,
                    TvId = ShowId,
                    SeasonId = 2,
                    SeasonNumber = 2,
                    EpisodeNumber = number,
                    Title = $"S02E{number:D2}",
                }
            );

        ctx.SaveChanges();
    }

    private void ScriptTmdb()
    {
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
            MockResponse.Json(
                HttpStatusCode.OK,
                """
                {"id":1,"results":[
                  {"id":"grp-absolute","name":"Absolute","order":1,"type":2,"episode_count":24,"group_count":1}
                ]}
                """
            )
        );

        // Absolute 13 is the first episode of season two in this ordering. The flat local run
        // would reach season two episode two instead, because season one is short a file.
        Handler.WhenGet(
            "/tv/episode_group/grp-absolute",
            MockResponse.Json(
                HttpStatusCode.OK,
                $$"""
                {"id":"grp-absolute","name":"Absolute","type":2,"episode_count":24,"group_count":1,
                 "description":"","groups":[{"id":"g1","name":"Absolute","order":1,"locked":false,"episodes":[
                  {{EpisodeRun(1, 1, 12)}},
                  {{EpisodeRun(2, 13, 12)}}
                 ]}]}
                """
            )
        );
    }

    private static string EpisodeRun(int seasonNumber, int startId, int count) =>
        string.Join(
            ",",
            Enumerable
                .Range(1, count)
                .Select(number =>
                    $$"""
                    {"id":{{startId
                        + number
                        - 1}},"episode_number":{{number}},"season_number":{{seasonNumber}},"name":"S{{seasonNumber:D2}}E{{number:D2}}","overview":"","air_date":null,"order":{{startId
                        + number
                        - 2}}}
                    """
                )
        );

    private MediaIdentificationService BuildService(MediaContext context)
    {
        ServiceCollection services = new();
        return new(
            context,
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>()
        );
    }

    [Fact]
    public async Task AbsoluteNumber_IsResolvedByTheProviderOrdering_NotTheLocalFlatRun()
    {
        SeedShowWithAGapInSeasonOne();
        ScriptTmdb();

        await using MediaContext context = new(_options);
        MediaIdentificationService service = BuildService(context);

        MovieFile parsed = new("/downloads/Ordering Probe - 13.mkv")
        {
            Title = ShowName,
            Season = 1,
            Episode = 13,
            IsSeries = true,
            IsSuccess = true,
        };

        (MovieOrEpisode match, string? imdbId)? result = await service.IdentifyAsync(
            parsed,
            MediaTypes.AnimeMediaType,
            duration: null,
            overrideTmdbId: null,
            seasonExplicit: false
        );

        result.Should().NotBeNull();
        result!.Value.match.SeasonNumber.Should().Be(2);
        result.Value.match.EpisodeNumber.Should().Be(1);
    }

    /// <summary>
    /// The provider hands back a group's episodes in curation order, not sequence, so the
    /// position each one declares is the only thing that says where it belongs. Reading the
    /// array as it arrives picks whichever episode happens to sit at that index.
    /// </summary>
    [Fact]
    public async Task GroupEpisodes_AreOrderedByTheirDeclaredPosition_NotArrayOrder()
    {
        SeedShowWithAGapInSeasonOne();

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
            MockResponse.Json(
                HttpStatusCode.OK,
                """
                {"id":1,"results":[
                  {"id":"grp-shuffled","name":"Absolute Order","order":1,"type":2,"episode_count":3,"group_count":1}
                ]}
                """
            )
        );

        // Positions 0,1,2 are S01E07, S02E04, S01E02 — deliberately not the array order, so
        // asking for the second episode must yield S02E04 rather than the array's second slot.
        Handler.WhenGet(
            "/tv/episode_group/grp-shuffled",
            MockResponse.Json(
                HttpStatusCode.OK,
                """
                {"id":"grp-shuffled","name":"Absolute Order","type":2,"episode_count":3,"group_count":1,
                 "description":"","groups":[{"id":"g1","name":"Absolute Order","order":1,"locked":false,"episodes":[
                  {"id":102,"episode_number":2,"season_number":1,"name":"S01E02","overview":"","air_date":null,"order":2},
                  {"id":107,"episode_number":7,"season_number":1,"name":"S01E07","overview":"","air_date":null,"order":0},
                  {"id":204,"episode_number":4,"season_number":2,"name":"S02E04","overview":"","air_date":null,"order":1}
                 ]}]}
                """
            )
        );

        await using MediaContext context = new(_options);
        MediaIdentificationService service = BuildService(context);

        MovieFile parsed = new("/downloads/Ordering Probe - 2.mkv")
        {
            Title = ShowName,
            Season = 9,
            Episode = 2,
            IsSeries = true,
            IsSuccess = true,
        };

        (MovieOrEpisode match, string? imdbId)? result = await service.IdentifyAsync(
            parsed,
            MediaTypes.AnimeMediaType,
            duration: null,
            overrideTmdbId: null,
            seasonExplicit: false
        );

        result.Should().NotBeNull();
        result!.Value.match.SeasonNumber.Should().Be(2);
        result.Value.match.EpisodeNumber.Should().Be(4);
    }

    /// <summary>
    /// Modelled on a real absolute-order group: 25 specials, then 28 episodes, then 10 more
    /// that continue the same season's numbering. An absolute number counts the main run
    /// only, so the 29th episode is season 1 episode 29 — counting the specials first shifts
    /// it by 25 and lands on season 1 episode 4.
    /// </summary>
    [Fact]
    public async Task Specials_AreNotCounted_InAnAbsoluteRun()
    {
        // Both candidate answers exist locally, so whichever the resolver picks comes back as
        // a real row and the test reads the choice rather than a lookup failure. The parsed
        // season is one the library does not have, which is what sends this down the absolute
        // path in the first place.
        using (MediaContext seed = new(_options))
        {
            seed.Tvs.Add(new() { Id = ShowId, Title = ShowName });
            seed.Seasons.Add(
                new()
                {
                    Id = 1,
                    TvId = ShowId,
                    SeasonNumber = 1,
                }
            );
            seed.Episodes.Add(
                new()
                {
                    Id = 9004,
                    TvId = ShowId,
                    SeasonId = 1,
                    SeasonNumber = 1,
                    EpisodeNumber = 4,
                    Title = "Where counting the specials lands",
                }
            );
            seed.Episodes.Add(
                new()
                {
                    Id = 9029,
                    TvId = ShowId,
                    SeasonId = 1,
                    SeasonNumber = 1,
                    EpisodeNumber = 29,
                    Title = "Twenty-ninth of the main run",
                }
            );
            seed.SaveChanges();
        }

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
            MockResponse.Json(
                HttpStatusCode.OK,
                """
                {"id":1,"results":[
                  {"id":"grp-abs","name":"Absolute Order","order":1,"type":2,"episode_count":63,"group_count":3}
                ]}
                """
            )
        );
        Handler.WhenGet(
            "/tv/episode_group/grp-abs",
            MockResponse.Json(
                HttpStatusCode.OK,
                $$"""
                {"id":"grp-abs","name":"Absolute Order","type":2,"episode_count":63,"group_count":3,
                 "description":"","groups":[
                  {"id":"g0","name":"Specials","order":0,"locked":false,"episodes":[{{SeasonRun(
                    0,
                    1,
                    25,
                    0
                )}}]},
                  {"id":"g1","name":"Season 01","order":1,"locked":false,"episodes":[{{SeasonRun(
                    1,
                    1,
                    28,
                    100
                )}}]},
                  {"id":"g2","name":"Season 02","order":2,"locked":false,"episodes":[{{SeasonRun(
                    1,
                    29,
                    10,
                    200
                )}}]}
                 ]}
                """
            )
        );

        await using MediaContext context = new(_options);
        MediaIdentificationService service = BuildService(context);

        MovieFile parsed = new("/downloads/Ordering Probe - 29.mkv")
        {
            Title = ShowName,
            Season = 2,
            Episode = 29,
            IsSeries = true,
            IsSuccess = true,
        };

        (MovieOrEpisode match, string? imdbId)? result = await service.IdentifyAsync(
            parsed,
            MediaTypes.AnimeMediaType,
            duration: null,
            overrideTmdbId: null,
            seasonExplicit: false
        );

        result.Should().NotBeNull();
        result!.Value.match.SeasonNumber.Should().Be(1);
        result.Value.match.EpisodeNumber.Should().Be(29);
    }

    /// <summary>
    /// Like <see cref="SeasonRun"/> but with a caller-supplied starting <c>order</c>, so
    /// multiple runs concatenated into one group's episode list get globally monotonic
    /// order values instead of each restarting at 0 and scrambling the curated sequence.
    /// </summary>
    private static string SeasonRunFromOrder(
        int seasonNumber,
        int firstEpisode,
        int count,
        int idBase,
        int orderStart
    ) =>
        string.Join(
            ",",
            Enumerable
                .Range(0, count)
                .Select(offset =>
                {
                    int episodeNumber = firstEpisode + offset;
                    int id = idBase + offset;
                    int order = orderStart + offset;
                    return $$"""
                    {"id":{{id}},"episode_number":{{episodeNumber}},"season_number":{{seasonNumber}},"name":"S{{seasonNumber}}E{{episodeNumber}}","overview":"","air_date":null,"order":{{order}}}
                    """;
                })
        );

    private static string SeasonRun(int seasonNumber, int firstEpisode, int count, int idBase) =>
        string.Join(
            ",",
            Enumerable
                .Range(0, count)
                .Select(offset =>
                {
                    int episodeNumber = firstEpisode + offset;
                    int id = idBase + offset;
                    return $$"""
                    {"id":{{id}},"episode_number":{{episodeNumber}},"season_number":{{seasonNumber}},"name":"S{{seasonNumber}}E{{episodeNumber}}","overview":"","air_date":null,"order":{{offset}}}
                    """;
                })
        );

    /// <summary>
    /// Reproduces the Chuunibyou demo Koi ga Shitai! Ren queue-corruption bug (2026-08-10):
    /// "Ren s02e18" is explicit about season 2, and season 2 only has 12 episodes. The flat
    /// local run used to answer anyway — it has no concept of "this season doesn't go that
    /// high", so it read straight through season one's 12 episodes and landed on season two's
    /// sixth, silently dispatching s02e18's encode to overwrite the real S02E06 output. An
    /// explicit season that TMDB doesn't have that episode in has to come back unmatched, not
    /// guessed.
    /// </summary>
    [Fact]
    public async Task AnExplicitSeasonEpisode_BeyondTheSeasonsRealCount_IsNotGuessedFromTheFlatRun()
    {
        SeedShowWithAGapInSeasonOne();

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
            $"/tv/{ShowId}/season/2/episode/18",
            MockResponse.Json(HttpStatusCode.NotFound, """{"status_code":34}""")
        );
        Handler.WhenGet(
            $"/tv/{ShowId}/episode_groups",
            MockResponse.Json(HttpStatusCode.OK, """{"id":1,"results":[]}""")
        );

        await using MediaContext context = new(_options);
        MediaIdentificationService service = BuildService(context);

        MovieFile parsed = new("/downloads/Ren s02e18 [Lite].mkv")
        {
            Title = ShowName,
            Season = 2,
            Episode = 18,
            IsSeries = true,
            IsSuccess = true,
        };

        (MovieOrEpisode match, string? imdbId)? result = await service.IdentifyAsync(
            parsed,
            MediaTypes.AnimeMediaType,
            duration: null,
            overrideTmdbId: null,
            seasonExplicit: true
        );

        result
            .Should()
            .BeNull(
                "season 2 episode 18 does not exist and must not be reinterpreted as a flat absolute index into every season's episodes combined"
            );
    }

    /// <summary>
    /// Reproduces the "Ah! My Goddess" intended-order group (TMDB show 912, group
    /// 5e049bfc0284200019b7064d): a recap special sits between real episodes 12 and 13
    /// of season 1. A flat-numbered file from the "Season 1" folder ("13.mkv") counts
    /// only the season's own episodes, so the special must not consume a slot in that
    /// count — it used to, which shifted every episode from 13 onward by one and
    /// eventually ran season 1 out of real episodes to match the higher file numbers.
    /// </summary>
    [Fact]
    public async Task AnInterleavedSpecial_DoesNotShiftTheSeasonsFlatNumbering()
    {
        using (MediaContext seed = new(_options))
        {
            seed.Tvs.Add(new() { Id = ShowId, Title = ShowName });
            seed.Seasons.Add(
                new()
                {
                    Id = 1,
                    TvId = ShowId,
                    SeasonNumber = 1,
                }
            );
            seed.SaveChanges();
        }

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
            $"/tv/{ShowId}/season/1/episode/13",
            MockResponse.Json(
                HttpStatusCode.OK,
                $$"""
                {"id":13,"name":"S01E13","overview":"","season_number":1,"episode_number":13,"air_date":null}
                """
            )
        );
        Handler.WhenGet(
            $"/tv/{ShowId}/episode_groups",
            MockResponse.Json(
                HttpStatusCode.OK,
                """
                {"id":1,"results":[
                  {"id":"grp-intended","name":"Intended Order","order":1,"type":6,"episode_count":14,"group_count":1}
                ]}
                """
            )
        );

        // Positions 1-12 are the real S01E01-E12, position 13 is a season-0 recap
        // special, position 14 is the real S01E13 — the exact "Ah! My Goddess" shape.
        Handler.WhenGet(
            "/tv/episode_group/grp-intended",
            MockResponse.Json(
                HttpStatusCode.OK,
                $$"""
                {"id":"grp-intended","name":"Intended Order","type":6,"episode_count":14,"group_count":1,
                 "description":"","groups":[{"id":"g1","name":"Season 1 (Intended Order)","order":1,"locked":false,"episodes":[
                  {{SeasonRun(1, 1, 12, 0)}},
                  {"id":9013,"episode_number":1,"season_number":0,"name":"Recap Special","overview":"","air_date":null,"order":12},
                  {"id":13,"episode_number":13,"season_number":1,"name":"S01E13","overview":"","air_date":null,"order":13}
                 ]}]}
                """
            )
        );

        await using MediaContext context = new(_options);
        MediaIdentificationService service = BuildService(context);

        MovieFile parsed = new("/downloads/Ah! My Goddess!/Season 1/13.mkv")
        {
            Title = ShowName,
            Season = 1,
            Episode = 13,
            IsSeries = true,
            IsSuccess = true,
        };

        (MovieOrEpisode match, string? imdbId)? result = await service.IdentifyAsync(
            parsed,
            MediaTypes.AnimeMediaType,
            duration: null,
            overrideTmdbId: null,
            seasonExplicit: true
        );

        result.Should().NotBeNull();
        result!.Value.match.SeasonNumber.Should().Be(1);
        result
            .Value.match.EpisodeNumber.Should()
            .Be(13, "the recap special at group position 13 must not consume season 1's 13th slot");
    }

    /// <summary>
    /// A file the filename parser could not read an episode number out of ("NCOP.mkv",
    /// a creditless-opening file inside the same batch's "Season 1" folder) reaches
    /// group resolution with episodeNumber 0. Indexing the group's same-season list
    /// with a raw <c>[episodeNumber - 1]</c> list indexer throws for a negative index
    /// instead of returning "no match" — an unhandled exception that aborted
    /// identification for the whole file before the TVDB fallback ever got a chance
    /// to run, reproduced live 2026-08-10 against the real "Ah! My Goddess" release.
    /// </summary>
    [Fact]
    public async Task AZeroEpisodeNumber_DoesNotThrow_ItComesBackUnmatched()
    {
        using (MediaContext seed = new(_options))
        {
            seed.Tvs.Add(new() { Id = ShowId, Title = ShowName });
            seed.Seasons.Add(
                new()
                {
                    Id = 1,
                    TvId = ShowId,
                    SeasonNumber = 1,
                }
            );
            seed.SaveChanges();
        }

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
            $"/tv/{ShowId}/season/1/episode/0",
            MockResponse.Json(HttpStatusCode.NotFound, """{"status_code":34}""")
        );
        Handler.WhenGet(
            $"/tv/{ShowId}/episode_groups",
            MockResponse.Json(
                HttpStatusCode.OK,
                """
                {"id":1,"results":[
                  {"id":"grp-intended","name":"Intended Order","order":1,"type":6,"episode_count":24,"group_count":1}
                ]}
                """
            )
        );
        Handler.WhenGet(
            "/tv/episode_group/grp-intended",
            MockResponse.Json(
                HttpStatusCode.OK,
                $$"""
                {"id":"grp-intended","name":"Intended Order","type":6,"episode_count":24,"group_count":1,
                 "description":"","groups":[{"id":"g1","name":"Season 1 (Intended Order)","order":1,"locked":false,"episodes":[
                  {{SeasonRun(1, 1, 24, 0)}}
                 ]}]}
                """
            )
        );

        await using MediaContext context = new(_options);
        MediaIdentificationService service = BuildService(context);

        MovieFile parsed = new("/downloads/Ah! My Goddess!/Season 1/NCOP.mkv")
        {
            Title = ShowName,
            Season = 1,
            Episode = 0,
            IsSeries = true,
            IsSuccess = true,
        };

        (MovieOrEpisode match, string? imdbId)? result = await service.IdentifyAsync(
            parsed,
            MediaTypes.AnimeMediaType,
            duration: null,
            overrideTmdbId: null,
            seasonExplicit: true
        );

        result.Should().BeNull("episode 0 is not a real episode in any season");
    }

    /// <summary>
    /// TMDB's own crowd-edited episode groups have twice given a wrong non-null answer
    /// for this exact show ("Ah! My Goddess" S00E02/E03 mislabelled S01E25/E26 by two
    /// different groups) — a non-null group answer is no longer trusted enough to skip
    /// TheTVDB. Both providers are scripted here to answer the same slot DIFFERENTLY;
    /// TVDB's answer must win because it is asked first, not because the group's was null.
    /// </summary>
    [Fact]
    public async Task TvdbWins_EvenWhenTmdbsOwnGroupAlsoHasAnAnswer_ForTheSameSlot()
    {
        ((NoMercy.Setup.Server.ApiKeyStore)NoMercy.Setup.Server.ApiKeyStore.Current).TvdbKey =
            "test-tvdb-key";

        using (MediaContext seed = new(_options))
        {
            seed.Tvs.Add(
                new()
                {
                    Id = ShowId,
                    Title = ShowName,
                    TvdbId = 78920,
                }
            );
            seed.Seasons.Add(
                new()
                {
                    Id = 1,
                    TvId = ShowId,
                    SeasonNumber = 1,
                }
            );
            seed.SaveChanges();
        }

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
            $"/tv/{ShowId}/season/1/episode/25",
            MockResponse.Json(HttpStatusCode.NotFound, """{"status_code":34}""")
        );

        // TVDB says this slot is S00E02 — the correct answer.
        Handler.WhenGet(
            $"/tv/{ShowId}/season/0/episode/2",
            MockResponse.Json(
                HttpStatusCode.OK,
                """
                {"id":9026,"name":"Ah! Urd's Little Romance","overview":"","season_number":0,"episode_number":2,"air_date":null}
                """
            )
        );
        string expiresAt = DateTime.UtcNow.AddMonths(1).ToString("O");
        Handler.WhenPost(
            "login",
            MockResponse.Json(
                HttpStatusCode.OK,
                $$"""
                {"status":"success","data":{"token":"test-token","expiresAt":"{{expiresAt}}"} }
                """
            )
        );
        Handler.WhenGet(
            "/series/78920/episodes/official",
            MockResponse.Json(
                HttpStatusCode.OK,
                $$"""
                {"status":"success","data":{"series":null,"episodes":[
                  {{TvdbSeasonRun(1, 1, 24, 0)}},
                  {"id":9026,"seriesId":78920,"number":2,"seasonNumber":0,"name":"Ah! Urd's Little Romance"}
                 ]} }
                """
            )
        );

        // TMDB's OWN group, if it were consulted, would give the WRONG answer — S01E25 —
        // proving TVDB won because it was asked first, not because the group had nothing.
        Handler.WhenGet(
            $"/tv/{ShowId}/episode_groups",
            MockResponse.Json(
                HttpStatusCode.OK,
                """
                {"id":1,"results":[
                  {"id":"grp-wrong","name":"Wrong Order","order":1,"type":6,"episode_count":26,"group_count":1}
                ]}
                """
            )
        );
        Handler.WhenGet(
            "/tv/episode_group/grp-wrong",
            MockResponse.Json(
                HttpStatusCode.OK,
                $$"""
                {"id":"grp-wrong","name":"Wrong Order","type":6,"episode_count":26,"group_count":1,
                 "description":"","groups":[{"id":"g1","name":"Season 1 (Wrong)","order":1,"locked":false,"episodes":[
                  {{SeasonRun(1, 1, 25, 0)}}
                 ]}]}
                """
            )
        );

        await using MediaContext context = new(_options);
        MediaIdentificationService service = BuildService(context);

        MovieFile parsed = new("/downloads/Ah! My Goddess!/Season 1/S01E25 - Ah! Urd's Little Romance.mkv")
        {
            Title = ShowName,
            Season = 1,
            Episode = 25,
            IsSeries = true,
            IsSuccess = true,
        };

        (MovieOrEpisode match, string? imdbId)? result = await service.IdentifyAsync(
            parsed,
            MediaTypes.AnimeMediaType,
            duration: null,
            overrideTmdbId: null,
            seasonExplicit: true
        );

        result.Should().NotBeNull();
        result!.Value.match.SeasonNumber.Should().Be(0, "TVDB is asked before TMDB's own groups, not only after they fail");
        result.Value.match.EpisodeNumber.Should().Be(2);
        Handler.Requests.Should().NotContain(
            r => r.Path.Contains("episode_group"),
            "the group endpoint must never be hit once TVDB already answered the slot"
        );
    }

    /// <summary>
    /// The other half of the "Ah! My Goddess" batch: the release keeps numbering
    /// straight past season 1's real 24 episodes into two trailing bonus specials
    /// ("25.mkv"/"26.mkv" for "Ah! Urd's Little Romance" / "Ah! Is My Heart
    /// Pounding..."), which the group — and TMDB's own default season data —
    /// both file under season 0, not season 1. Once the season's real episodes are
    /// exhausted, the excess file numbers must resolve to those trailing entries
    /// at their own identity rather than come back unmatched or mislabelled S01.
    /// </summary>
    [Fact]
    public async Task FlatNumberingPastTheSeasonsRealCount_ResolvesToTheTrailingSpecials()
    {
        using (MediaContext seed = new(_options))
        {
            seed.Tvs.Add(new() { Id = ShowId, Title = ShowName });
            seed.Seasons.Add(
                new()
                {
                    Id = 1,
                    TvId = ShowId,
                    SeasonNumber = 1,
                }
            );
            seed.SaveChanges();
        }

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
            $"/tv/{ShowId}/season/1/episode/25",
            MockResponse.Json(HttpStatusCode.NotFound, """{"status_code":34}""")
        );
        Handler.WhenGet(
            $"/tv/{ShowId}/season/0/episode/2",
            MockResponse.Json(
                HttpStatusCode.OK,
                """
                {"id":9026,"name":"Ah! Urd's Little Romance","overview":"","season_number":0,"episode_number":2,"air_date":null}
                """
            )
        );
        Handler.WhenGet(
            $"/tv/{ShowId}/episode_groups",
            MockResponse.Json(
                HttpStatusCode.OK,
                """
                {"id":1,"results":[
                  {"id":"grp-intended","name":"Intended Order","order":1,"type":6,"episode_count":26,"group_count":1}
                ]}
                """
            )
        );

        // The real shape: 12 real episodes, ONE interleaved recap special (season 0, sitting
        // BETWEEN real episodes 12 and 13 in curation order), 12 more real episodes, THEN
        // the two genuinely trailing bonus specials. A naive "every non-matching-season
        // entry, in order" filter put the interleaved recap ahead of the trailing specials
        // in that list — file 25 resolved to the recap instead of "Urd's Little Romance".
        // Only a fixture with BOTH an interleaved AND a trailing special proves the fix.
        Handler.WhenGet(
            "/tv/episode_group/grp-intended",
            MockResponse.Json(
                HttpStatusCode.OK,
                $$"""
                {"id":"grp-intended","name":"Intended Order","type":6,"episode_count":27,"group_count":1,
                 "description":"","groups":[{"id":"g1","name":"Season 1 (Intended Order)","order":1,"locked":false,"episodes":[
                  {{SeasonRunFromOrder(1, 1, 12, 0, 0)}},
                  {"id":9013,"episode_number":1,"season_number":0,"name":"Ah! An Exchange Diary with the Goddess?","overview":"","air_date":null,"order":12},
                  {{SeasonRunFromOrder(1, 13, 12, 100, 13)}},
                  {"id":9026,"episode_number":2,"season_number":0,"name":"Ah! Urd's Little Romance","overview":"","air_date":null,"order":25},
                  {"id":9027,"episode_number":3,"season_number":0,"name":"Ah! Is My Heart Pounding...","overview":"","air_date":null,"order":26}
                 ]}]}
                """
            )
        );

        await using MediaContext context = new(_options);
        MediaIdentificationService service = BuildService(context);

        MovieFile parsed = new("/downloads/Ah! My Goddess!/Season 1/S01E25 - Ah! Urd's Little Romance.mkv")
        {
            Title = ShowName,
            Season = 1,
            Episode = 25,
            IsSeries = true,
            IsSuccess = true,
        };

        (MovieOrEpisode match, string? imdbId)? result = await service.IdentifyAsync(
            parsed,
            MediaTypes.AnimeMediaType,
            duration: null,
            overrideTmdbId: null,
            seasonExplicit: true
        );

        result.Should().NotBeNull();
        result!.Value.match.SeasonNumber.Should().Be(0, "TMDB files this bonus episode under Specials, not season 1");
        result.Value.match.EpisodeNumber.Should().Be(2);
    }

    /// <summary>
    /// The real "Ah! My Goddess" episode group has THREE sub-groups, not one per season:
    /// "Special Episodes" (2 eps), "Season 1" (27 eps), "Season 2" (24 eps) — confirmed via
    /// https://www.themoviedb.org/tv/912-oh-my-goddess/episode_group/5e049bf84c1d9a0019dbcff6.
    /// Picking a sub-group by raw position ("index seasonNumber-1") assumed the season
    /// sub-groups sort first with nothing else mixed in. Scripted here with "Special
    /// Episodes" sorting FIRST — a real, plausible ordering this codebase cannot rule out
    /// by scraping TMDB's website — every file for the whole show used to come back
    /// unmatched under that ordering, not just this one. Selecting the sub-group whose
    /// episodes are actually dominated by the target season, instead of by position, fixes
    /// it regardless of how the provider ordered its sub-groups.
    /// </summary>
    [Fact]
    public async Task ASubGroupThatIsNotOneOfTheSeasons_DoesNotShiftEveryLaterSeasonOutOfPosition()
    {
        using (MediaContext seed = new(_options))
        {
            seed.Tvs.Add(new() { Id = ShowId, Title = ShowName });
            seed.Seasons.Add(
                new()
                {
                    Id = 1,
                    TvId = ShowId,
                    SeasonNumber = 1,
                }
            );
            seed.SaveChanges();
        }

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
            $"/tv/{ShowId}/season/1/episode/25",
            MockResponse.Json(HttpStatusCode.NotFound, """{"status_code":34}""")
        );
        Handler.WhenGet(
            $"/tv/{ShowId}/season/0/episode/2",
            MockResponse.Json(
                HttpStatusCode.OK,
                """
                {"id":9026,"name":"Ah! Urd's Little Romance","overview":"","season_number":0,"episode_number":2,"air_date":null}
                """
            )
        );
        Handler.WhenGet(
            $"/tv/{ShowId}/episode_groups",
            MockResponse.Json(
                HttpStatusCode.OK,
                """
                {"id":1,"results":[
                  {"id":"grp-intended","name":"Intended Order","order":1,"type":6,"episode_count":53,"group_count":3}
                ]}
                """
            )
        );

        // "Special Episodes" is listed FIRST (order:0) — Season 1 is order:1, not order:0.
        // A positional picker asking for "index seasonNumber-1 == 0" would grab Specials
        // instead of Season 1, and every file in the whole show would come back unmatched.
        Handler.WhenGet(
            "/tv/episode_group/grp-intended",
            MockResponse.Json(
                HttpStatusCode.OK,
                $$"""
                {"id":"grp-intended","name":"Intended Order","type":6,"episode_count":53,"group_count":3,
                 "description":"","groups":[
                  {"id":"g0","name":"Special Episodes","order":0,"locked":false,"episodes":[
                    {"id":9106,"episode_number":6,"season_number":0,"name":"Ah! The One-Winged Angel Descends!","overview":"","air_date":null,"order":0},
                    {"id":9107,"episode_number":7,"season_number":0,"name":"Ah! Two of Us, Together in Joy!","overview":"","air_date":null,"order":1}
                   ]},
                  {"id":"g1","name":"Season 1 (Intended Order)","order":1,"locked":false,"episodes":[
                    {{SeasonRunFromOrder(1, 1, 24, 0, 0)}},
                    {"id":9026,"episode_number":2,"season_number":0,"name":"Ah! Urd's Little Romance","overview":"","air_date":null,"order":24}
                   ]}
                 ]}
                """
            )
        );

        await using MediaContext context = new(_options);
        MediaIdentificationService service = BuildService(context);

        MovieFile parsed = new("/downloads/Ah! My Goddess!/Season 1/S01E25 - Ah! Urd's Little Romance.mkv")
        {
            Title = ShowName,
            Season = 1,
            Episode = 25,
            IsSeries = true,
            IsSuccess = true,
        };

        (MovieOrEpisode match, string? imdbId)? result = await service.IdentifyAsync(
            parsed,
            MediaTypes.AnimeMediaType,
            duration: null,
            overrideTmdbId: null,
            seasonExplicit: true
        );

        result.Should().NotBeNull("a non-season sub-group sorting first must not make season 1 unresolvable");
        result!.Value.match.SeasonNumber.Should().Be(0);
        result.Value.match.EpisodeNumber.Should().Be(2);
    }

    /// <summary>
    /// When TMDB has genuinely nothing — no default season/episode for this pair and no
    /// episode group either — the last resort before giving up is TheTVDB, curated
    /// separately from TMDB and, for anime, often the one with the correct split. TVDB
    /// only says WHICH (season, episode) pair is right; the row is still built from
    /// TMDB's own matching episode so every stored episode stays TMDB-keyed.
    /// </summary>
    [Fact]
    public async Task WhenTmdbHasNothing_TheTvdbSplitIsTriedBeforeGivingUp()
    {
        ((NoMercy.Setup.Server.ApiKeyStore)NoMercy.Setup.Server.ApiKeyStore.Current).TvdbKey =
            "test-tvdb-key";

        using (MediaContext seed = new(_options))
        {
            seed.Tvs.Add(
                new()
                {
                    Id = ShowId,
                    Title = ShowName,
                    TvdbId = 78920,
                }
            );
            seed.Seasons.Add(
                new()
                {
                    Id = 1,
                    TvId = ShowId,
                    SeasonNumber = 1,
                }
            );
            seed.SaveChanges();
        }

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
            $"/tv/{ShowId}/season/1/episode/25",
            MockResponse.Json(HttpStatusCode.NotFound, """{"status_code":34}""")
        );
        Handler.WhenGet(
            $"/tv/{ShowId}/episode_groups",
            MockResponse.Json(HttpStatusCode.OK, """{"id":1,"results":[]}""")
        );
        Handler.WhenGet(
            $"/tv/{ShowId}/season/0/episode/2",
            MockResponse.Json(
                HttpStatusCode.OK,
                """
                {"id":9026,"name":"Ah! Urd's Little Romance","overview":"","season_number":0,"episode_number":2,"air_date":null}
                """
            )
        );

        string expiresAt = DateTime.UtcNow.AddMonths(1).ToString("O");
        Handler.WhenPost(
            "login",
            MockResponse.Json(
                HttpStatusCode.OK,
                $$"""
                {"status":"success","data":{"token":"test-token","expiresAt":"{{expiresAt}}"} }
                """
            )
        );
        Handler.WhenGet(
            "/series/78920/episodes/official",
            MockResponse.Json(
                HttpStatusCode.OK,
                $$"""
                {"status":"success","data":{"series":null,"episodes":[
                  {{TvdbSeasonRun(1, 1, 24, 0)}},
                  {"id":9026,"seriesId":78920,"number":2,"seasonNumber":0,"name":"Ah! Urd's Little Romance"},
                  {"id":9027,"seriesId":78920,"number":3,"seasonNumber":0,"name":"Ah! Is My Heart Pounding..."}
                 ]} }
                """
            )
        );

        await using MediaContext context = new(_options);
        MediaIdentificationService service = BuildService(context);

        MovieFile parsed = new("/downloads/Ah! My Goddess!/Season 1/S01E25 - Ah! Urd's Little Romance.mkv")
        {
            Title = ShowName,
            Season = 1,
            Episode = 25,
            IsSeries = true,
            IsSuccess = true,
        };

        (MovieOrEpisode match, string? imdbId)? result = await service.IdentifyAsync(
            parsed,
            MediaTypes.AnimeMediaType,
            duration: null,
            overrideTmdbId: null,
            seasonExplicit: true
        );

        result.Should().NotBeNull();
        result!.Value.match.SeasonNumber.Should().Be(0, "TVDB is the one that had this bonus episode when TMDB had neither a default nor a group answer");
        result.Value.match.EpisodeNumber.Should().Be(2);
    }

    private static string TvdbSeasonRun(int seasonNumber, int firstEpisode, int count, int idBase) =>
        string.Join(
            ",",
            Enumerable
                .Range(0, count)
                .Select(offset =>
                {
                    int episodeNumber = firstEpisode + offset;
                    int id = idBase + offset;
                    return $$"""
                    {"id":{{id}},"seriesId":78920,"number":{{episodeNumber}},"seasonNumber":{{seasonNumber}},"name":"S{{seasonNumber}}E{{episodeNumber}}"}
                    """;
                })
        );

    [Fact]
    public async Task TheProviderOrdering_IsRequested_BeforeAnythingIsInferredLocally()
    {
        SeedShowWithAGapInSeasonOne();
        ScriptTmdb();

        await using MediaContext context = new(_options);
        MediaIdentificationService service = BuildService(context);

        MovieFile parsed = new("/downloads/Ordering Probe - 13.mkv")
        {
            Title = ShowName,
            Season = 1,
            Episode = 13,
            IsSeries = true,
            IsSuccess = true,
        };

        await service.IdentifyAsync(
            parsed,
            MediaTypes.AnimeMediaType,
            duration: null,
            overrideTmdbId: null,
            seasonExplicit: false
        );

        Handler.RequestCountFor("episode_group").Should().BeGreaterThan(0);
    }

    /// <summary>
    /// The real "Ah! My Goddess" release also has its own "\Specials\" folder with its OWN
    /// flat numbering (S00E01-E12) that has nothing to do with TMDB's real season-0
    /// numbering — its own E02 is "Midsummer Night's Dream" (from an unrelated 1993 OVA
    /// TMDB tracks as a totally different show), while TMDB's REAL S00E02 for THIS show is
    /// "Ah! Urd's Little Romance". Before this fix, the direct TMDB season/episode lookup
    /// accepted the numeric match with no content check, silently attaching the "Midsummer
    /// Night's Dream" file to the "Urd's Little Romance" episode row — the exact same row
    /// "\Season 1\...S01E25 - Ah! Urd's Little Romance.mkv" already correctly resolves to,
    /// which is why the dashboard showed the two unrelated files as "linked by id".
    /// </summary>
    [Fact]
    public async Task AFileWhoseOwnTitleContradictsTheNumberMatch_IsNotSilentlyAttachedToIt()
    {
        // A show id never used elsewhere in this class — the on-disk response cache the
        // harness's HttpClientProvider sits on top of is keyed by URL and survives across
        // separate `dotnet test` runs, so reusing the shared ShowId const here would let an
        // earlier run's cached "Intended Order" group body silently answer this test's
        // deliberately-empty episode_groups mock instead of the mock itself.
        const int freshShowId = 771099;

        using (MediaContext seed = new(_options))
        {
            seed.Tvs.Add(new() { Id = freshShowId, Title = ShowName });
            seed.SaveChanges();
        }

        Handler.WhenGet(
            "/search/tv",
            MockResponse.Json(
                HttpStatusCode.OK,
                $$"""
                {"page":1,"total_pages":1,"total_results":1,"results":[
                  {"id":{{freshShowId}},"name":"{{ShowName}}","first_air_date":"2020-01-01"}
                ]}
                """
            )
        );
        // TMDB genuinely has a real S00E02 for this show — it just isn't this file's content.
        Handler.WhenGet(
            $"/tv/{freshShowId}/season/0/episode/2",
            MockResponse.Json(
                HttpStatusCode.OK,
                """
                {"id":9026,"name":"Ah! Urd's Little Romance","overview":"","season_number":0,"episode_number":2,"air_date":null}
                """
            )
        );
        Handler.WhenGet(
            $"/tv/{freshShowId}/episode_groups",
            MockResponse.Json(HttpStatusCode.OK, """{"id":1,"results":[]}""")
        );

        await using MediaContext context = new(_options);
        MediaIdentificationService service = BuildService(context);

        MovieFile parsed = new(
            "/downloads/Ah! My Goddess!/Specials/Ah! My Goddess - S00E02 - Midsummer Night's Dream.mkv"
        )
        {
            Title = ShowName,
            Season = 0,
            Episode = 2,
            IsSeries = true,
            IsSuccess = true,
        };

        (MovieOrEpisode match, string? imdbId)? result = await service.IdentifyAsync(
            parsed,
            MediaTypes.AnimeMediaType,
            duration: null,
            overrideTmdbId: null,
            seasonExplicit: true
        );

        result
            .Should()
            .BeNull(
                "\"Midsummer Night's Dream\" is not \"Ah! Urd's Little Romance\" — a numeric coincidence must not attach this file to that episode"
            );
    }

    /// <summary>
    /// The same title check must not reject a CORRECT match just because the release's own
    /// title text and TMDB's official title disagree on translation — a real case from this
    /// exact show: the release keeps the untranslated honorific "Onee-Sama" where TMDB's
    /// title says "Big Sister". Same episode, different translation, must still match.
    /// </summary>
    [Fact]
    public async Task ATranslationDifference_DoesNotRejectTheOtherwiseCorrectMatch()
    {
        // Fresh id — see the comment in AFileWhoseOwnTitleContradictsTheNumberMatch_
        // IsNotSilentlyAttachedToIt for why the shared ShowId const isn't safe here.
        const int freshShowId = 771098;

        using (MediaContext seed = new(_options))
        {
            seed.Tvs.Add(new() { Id = freshShowId, Title = ShowName });
            seed.Seasons.Add(
                new()
                {
                    Id = 1,
                    TvId = freshShowId,
                    SeasonNumber = 1,
                }
            );
            seed.SaveChanges();
        }

        Handler.WhenGet(
            "/search/tv",
            MockResponse.Json(
                HttpStatusCode.OK,
                $$"""
                {"page":1,"total_pages":1,"total_results":1,"results":[
                  {"id":{{freshShowId}},"name":"{{ShowName}}","first_air_date":"2020-01-01"}
                ]}
                """
            )
        );
        Handler.WhenGet(
            $"/tv/{freshShowId}/season/1/episode/13",
            MockResponse.Json(
                HttpStatusCode.OK,
                """
                {"id":9013,"name":"Ah! Who Does Big Sister Belong To?","overview":"","season_number":1,"episode_number":13,"air_date":null}
                """
            )
        );

        await using MediaContext context = new(_options);
        MediaIdentificationService service = BuildService(context);

        MovieFile parsed = new(
            "/downloads/Ah! My Goddess!/Season 1/Ah! My Goddess - S01E13 - Ah! Who Does Onee-Sama Belong To.mkv"
        )
        {
            Title = ShowName,
            Season = 1,
            Episode = 13,
            IsSeries = true,
            IsSuccess = true,
        };

        (MovieOrEpisode match, string? imdbId)? result = await service.IdentifyAsync(
            parsed,
            MediaTypes.AnimeMediaType,
            duration: null,
            overrideTmdbId: null,
            seasonExplicit: true
        );

        result.Should().NotBeNull("Onee-Sama and Big Sister are the same episode, only translated differently");
        result!.Value.match.SeasonNumber.Should().Be(1);
        result.Value.match.EpisodeNumber.Should().Be(13);
    }
}
