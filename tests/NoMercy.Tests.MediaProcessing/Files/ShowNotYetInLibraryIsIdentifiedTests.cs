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
/// The dashboard's Add content dialog lists a folder of a show the server does
/// not hold yet, and that is the moment the owner needs each row to say which
/// episode it is. Identification reads only (it must not import), so a show
/// missing from the library used to end the lookup and every row came back with
/// no season, no episode and no still.
/// </summary>
[Collection("HttpClientProvider")]
public class ShowNotYetInLibraryIsIdentifiedTests : ProviderHttpHarness
{
    private const int ShowId = 771003;
    private const string ReleaseFolder = "The.Ark.S01.1080p.AMZN.WEBRip.DDP5.1.x264-NTb[rartv]";
    private const string FirstFile = "The.Ark.S01E01.1080p.AMZN.WEB-DL.DDP5.1.H.264-NTb.mkv";

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MediaContext> _options;

    public ShowNotYetInLibraryIsIdentifiedTests()
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

    private static FilenameResolver Resolver() =>
        new(
            new FilenameParserPipeline(
                new IFilenameParseAdapter[]
                {
                    new EpisodePrefixAdapter(),
                    new EpisodeWordAdapter(),
                    new CrossFormatAdapter(),
                    new SeasonEpisodeAdapter(),
                    new SeasonSpecialAdapter(),
                    new AnimeAbsoluteAdapter(),
                    new EpisodeShortFormAdapter(),
                    new SpecialsAdapter(),
                    new SeasonPackAdapter(),
                    new PartAdapter(),
                    new MovieDetectorAdapter(),
                }
            )
        );

    private static ResolvedName Resolve(string file)
    {
        string directory = $"/downloads/{ReleaseFolder}";
        return Resolver().Resolve(file, directory, $"{directory}/{file}", MediaTypes.TvMediaType);
    }

    private void ScriptTmdb()
    {
        Handler.WhenGet(
            "/search/tv",
            MockResponse.Json(
                HttpStatusCode.OK,
                $$"""
                {"page":1,"total_pages":1,"total_results":1,"results":[
                  {"id":{{ShowId}},"name":"The Ark","first_air_date":"2023-02-01"}
                ]}
                """
            )
        );
        Handler.WhenGet(
            $"/tv/{ShowId}/season/1/episode/1",
            MockResponse.Json(
                HttpStatusCode.OK,
                """
                {"id":9001,"name":"Pilot","overview":"","season_number":1,"episode_number":1,"still_path":"/still1.jpg","air_date":"2023-02-01"}
                """
            )
        );
        Handler.WhenGet(
            $"/tv/{ShowId}/season/1/episode/2",
            MockResponse.Json(
                HttpStatusCode.OK,
                """
                {"id":9002,"name":"Second","overview":"","season_number":1,"episode_number":2,"still_path":"/still2.jpg","air_date":"2023-02-08"}
                """
            )
        );
    }

    [Fact]
    public void The_release_name_parses_to_title_season_and_episode()
    {
        ResolvedName resolved = Resolve(FirstFile);

        resolved.Parsed.Title.Should().Be("The Ark");
        resolved.Parsed.Season.Should().Be(1);
        resolved.Parsed.Episode.Should().Be(1);
        resolved.SeasonExplicit.Should().BeTrue();
    }

    [Theory]
    [InlineData(FirstFile, 1, "Pilot")]
    [InlineData("The.Ark.S01E02.1080p.AMZN.WEB-DL.DDP5.1.H.264-NTb.mkv", 2, "Second")]
    public async Task A_show_the_server_does_not_hold_still_identifies_each_episode(
        string file,
        int episode,
        string title
    )
    {
        ScriptTmdb();
        ResolvedName resolved = Resolve(file);

        await using MediaContext context = new(_options);
        MediaIdentificationService service = new(
            context,
            new ServiceCollection()
                .BuildServiceProvider()
                .GetRequiredService<IServiceScopeFactory>()
        );

        (MovieOrEpisode match, string? imdbId)? result = await service.IdentifyAsync(
            resolved.Parsed,
            MediaTypes.TvMediaType,
            duration: null,
            resolved.OverrideTmdbId,
            resolved.SeasonExplicit,
            resolved.AirDate
        );

        result.Should().NotBeNull();
        result!.Value.match.SeasonNumber.Should().Be(1);
        result.Value.match.EpisodeNumber.Should().Be(episode);
        result.Value.match.Title.Should().Be(title);
        result.Value.match.ShowName.Should().Be("The Ark");

        // Identification reads. Nothing about this show may be written.
        (await context.Tvs.AnyAsync())
            .Should()
            .BeFalse();
        (await context.Episodes.AnyAsync()).Should().BeFalse();
    }

    /// <summary>
    /// The same release names against a show the server does hold. The tail after
    /// the marker is "1080p.AMZN.WEB-DL...", release vocabulary and not an episode
    /// title, so it must not be used to rule out the episode it names.
    /// </summary>
    [Fact]
    public async Task Release_vocabulary_after_the_marker_does_not_rule_out_the_episode()
    {
        ScriptTmdb();
        await using (MediaContext seed = new(_options))
        {
            seed.Tvs.Add(new() { Id = ShowId, Title = "The Ark" });
            await seed.SaveChangesAsync();
        }

        ResolvedName resolved = Resolve(FirstFile);

        await using MediaContext context = new(_options);
        MediaIdentificationService service = new(
            context,
            new ServiceCollection()
                .BuildServiceProvider()
                .GetRequiredService<IServiceScopeFactory>()
        );

        (MovieOrEpisode match, string? imdbId)? result = await service.IdentifyAsync(
            resolved.Parsed,
            MediaTypes.TvMediaType,
            duration: null,
            resolved.OverrideTmdbId,
            resolved.SeasonExplicit,
            resolved.AirDate
        );

        result.Should().NotBeNull();
        result!.Value.match.Title.Should().Be("Pilot");
    }
}
