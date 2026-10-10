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
/// "Show.Name.102" is season one episode two to the scene, and "One Piece 102" is
/// the hundred and second episode to a fansub. The two conventions write the
/// identical three digits, so no rule over the NAME can tell them apart — every
/// attempt reads a long-running show's episode 310 as its season three episode
/// ten, which is a real episode and therefore a silent, permanent mismatch.
/// <para>
/// What tells them apart is the show, so the number is split only here, where the
/// show is known, and only after every reading of it as a whole number has
/// failed. These tests make the two readings disagree and pin which one answers.
/// </para>
/// </summary>
[Collection("HttpClientProvider")]
public class SceneNumberResolutionTests : ProviderHttpHarness
{
    private const int ShowId = 771002;
    private const string ShowName = "Scene Number Probe";

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MediaContext> _options;

    public SceneNumberResolutionTests()
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

    /// <param name="episodesPerSeason">
    /// How long each season is. A short show cannot contain absolute episode 102,
    /// so the split is the only reading left; a long one can contain it, and then
    /// the whole number is what it was.
    /// </param>
    private void SeedShow(params int[] episodesPerSeason)
    {
        using MediaContext ctx = new(_options);
        ctx.Tvs.Add(new() { Id = ShowId, Title = ShowName });

        int episodeId = 1;
        for (int seasonNumber = 1; seasonNumber <= episodesPerSeason.Length; seasonNumber++)
        {
            ctx.Seasons.Add(
                new()
                {
                    Id = seasonNumber,
                    TvId = ShowId,
                    SeasonNumber = seasonNumber,
                }
            );

            foreach (int number in Enumerable.Range(1, episodesPerSeason[seasonNumber - 1]))
                ctx.Episodes.Add(
                    new()
                    {
                        Id = episodeId++,
                        TvId = ShowId,
                        SeasonId = seasonNumber,
                        SeasonNumber = seasonNumber,
                        EpisodeNumber = number,
                        Title = $"S{seasonNumber:D2}E{number:D2}",
                    }
                );
        }

        ctx.SaveChanges();
    }

    /// <summary>No absolute ordering published, which is the ordinary case for a
    /// show nobody numbers straight through.</summary>
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
            MockResponse.Json(HttpStatusCode.OK, """{"id":1,"results":[]}""")
        );
    }

    private MediaIdentificationService BuildService(MediaContext context)
    {
        ServiceCollection services = new();
        return new(
            context,
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>()
        );
    }

    private async Task<(MovieOrEpisode match, string? imdbId)?> Identify(int number)
    {
        await using MediaContext context = new(_options);
        MediaIdentificationService service = BuildService(context);

        MovieFile parsed = new($"/downloads/{ShowName}.{number}.mkv")
        {
            Title = ShowName,
            Season = 1,
            Episode = number,
            IsSeries = true,
            IsSuccess = true,
        };

        return await service.IdentifyAsync(
            parsed,
            MediaTypes.TvMediaType,
            duration: null,
            overrideTmdbId: null,
            seasonExplicit: false
        );
    }

    /// <summary>
    /// Two seasons of twelve. Absolute 102 is off the end of a show with
    /// twenty-four episodes, so the number was never one number.
    /// </summary>
    [Fact]
    public async Task A_number_that_cannot_be_an_episode_is_read_as_season_and_episode()
    {
        SeedShow(12, 12);
        ScriptTmdb();

        (MovieOrEpisode match, string? imdbId)? result = await Identify(102);

        result.Should().NotBeNull();
        result!.Value.match.SeasonNumber.Should().Be(1);
        result.Value.match.EpisodeNumber.Should().Be(2);
    }

    [Fact]
    public async Task The_season_half_is_the_leading_digits()
    {
        SeedShow(12, 12, 12, 12);
        ScriptTmdb();

        (MovieOrEpisode match, string? imdbId)? result = await Identify(401);

        result.Should().NotBeNull();
        result!.Value.match.SeasonNumber.Should().Be(4);
        result.Value.match.EpisodeNumber.Should().Be(1);
    }

    /// <summary>
    /// The same three digits against a show long enough to hold them. This is the
    /// case the rule must not touch: 154 counted straight through is a real
    /// episode of this show, and season one episode fifty-four is a different
    /// real episode. Splitting here is how "BLEACH - 154" lands on the wrong one.
    /// </summary>
    [Fact]
    public async Task A_number_the_show_can_hold_stays_one_number()
    {
        SeedShow(60, 60, 60, 60);
        ScriptTmdb();

        (MovieOrEpisode match, string? imdbId)? result = await Identify(154);

        result.Should().NotBeNull();
        result!.Value.match.SeasonNumber.Should().Be(3);
        result.Value.match.EpisodeNumber.Should().Be(34);
    }

    /// <summary>
    /// A name that spelled its season out is not a scene number — its episode is
    /// simply high. Splitting an explicit S01E102 would answer a question nobody
    /// asked.
    /// </summary>
    [Fact]
    public async Task An_explicit_season_is_never_split()
    {
        SeedShow(12, 12);
        ScriptTmdb();

        Handler.WhenGet(
            $"/tv/{ShowId}/season/1/episode/102",
            MockResponse.Status(HttpStatusCode.NotFound)
        );

        await using MediaContext context = new(_options);
        MediaIdentificationService service = BuildService(context);

        MovieFile parsed = new($"/downloads/{ShowName}.S01E102.mkv")
        {
            Title = ShowName,
            Season = 1,
            Episode = 102,
            IsSeries = true,
            IsSuccess = true,
        };

        (MovieOrEpisode match, string? imdbId)? result = await service.IdentifyAsync(
            parsed,
            MediaTypes.TvMediaType,
            duration: null,
            overrideTmdbId: null,
            seasonExplicit: true
        );

        result.Should().BeNull();
    }

    /// <summary>
    /// A trailing zero means the split names episode zero, which no season has.
    /// The number stays whole rather than becoming season four episode nothing.
    /// </summary>
    [Fact]
    public async Task A_split_that_names_no_episode_is_not_a_split()
    {
        SeedShow(12, 12);
        ScriptTmdb();
        Handler.WhenGet(
            $"/tv/{ShowId}/season/1/episode/400",
            MockResponse.Status(HttpStatusCode.NotFound)
        );

        (MovieOrEpisode match, string? imdbId)? result = await Identify(400);

        result.Should().BeNull();
    }

    [Theory]
    [InlineData(MediaTypes.TvMediaType)]
    [InlineData(MediaTypes.AnimeMediaType)]
    public async Task An_override_with_no_matching_episode_returns_unidentified(string libraryType)
    {
        SeedShow();
        Handler.When(
            request =>
                request.Method == HttpMethod.Get
                && request.RequestUri?.AbsolutePath.EndsWith(
                    $"/tv/{ShowId}",
                    StringComparison.Ordinal
                ) == true,
            MockResponse.Json(
                HttpStatusCode.OK,
                $$"""{"id":{{ShowId}},"name":"{{ShowName}}","first_air_date":"2020-01-01"}"""
            )
        );
        Handler.WhenGet(
            $"/tv/{ShowId}/episode_groups",
            MockResponse.Json(HttpStatusCode.OK, """{"id":1,"results":[]}""")
        );
        Handler.WhenGet(
            $"/tv/{ShowId}/season/1/episode/999",
            MockResponse.Status(HttpStatusCode.NotFound)
        );

        await using MediaContext context = new(_options);
        MediaIdentificationService service = BuildService(context);
        MovieFile parsed = new($"/downloads/{ShowName}.999.mkv")
        {
            Title = ShowName,
            Episode = 999,
            IsSeries = true,
            IsSuccess = true,
        };

        (MovieOrEpisode match, string? imdbId)? result = await service.IdentifyAsync(
            parsed,
            libraryType,
            duration: null,
            overrideTmdbId: ShowId,
            seasonExplicit: false
        );

        result.Should().BeNull();
        Handler.RequestCountFor($"/tv/{ShowId}/season/1/episode/999").Should().Be(1);
    }
}
