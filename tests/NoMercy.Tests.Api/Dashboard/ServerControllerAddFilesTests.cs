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
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Movies;
using NoMercy.Database.Models.TvShows;
using NoMercy.Tests.Api.Infrastructure;
using Xunit;

namespace NoMercy.Tests.Api.Dashboard;

/// <summary>
/// The import/"add files" path (ServerController.AddFiles) is a deliberate manual
/// import: it always dispatches a <c>VideoEncodeJob</c> per file against the
/// explicit source path, because the file is typically staged off the library root
/// (source_driver_id set) where a FileRescanJob — which only re-walks existing
/// library folders — could never see it. A configured
/// <see cref="Library.EncodePresetId"/> narrows the encode to that preset;
/// <see cref="Library.AutoEncodeOnScan"/> gates only the automatic file-watcher
/// path (AutoEncodeSubscriber), never this manual import. QueueRunner is a real
/// singleton in the test host (resolved via ServerController's own constructor
/// parameter), so a Dispatch call writes a real row into the shared queue.db —
/// asserted here by filtering on the unique per-test LibraryId embedded in every
/// job payload.
/// </summary>
[Trait("Category", "DashboardServer")]
public class ServerControllerAddFilesTests : IClassFixture<NoMercyApiFactory>, IAsyncLifetime
{
    private readonly HttpClient _authed;

    private readonly Ulid _autoEncodeLibraryId = Ulid.NewUlid();
    private readonly Ulid _manualLibraryId = Ulid.NewUlid();
    private readonly Ulid _tvLibraryId = Ulid.NewUlid();
    private readonly Ulid _folderId = Ulid.NewUlid();
    private readonly Ulid _presetId = Ulid.NewUlid();

    public ServerControllerAddFilesTests(NoMercyApiFactory factory)
    {
        _authed = factory.CreateClient().AsAuthenticated();
    }

    public async Task InitializeAsync()
    {
        await using MediaContext ctx = new();

        Library autoEncodeLibrary = new()
        {
            Id = _autoEncodeLibraryId,
            Title = "Auto Encode Movies",
            Type = "movie",
            AutoEncodeOnScan = true,
            EncodePresetId = _presetId,
        };
        Library manualLibrary = new()
        {
            Id = _manualLibraryId,
            Title = "Manual Movies",
            Type = "movie",
            AutoEncodeOnScan = false,
        };

        ctx.Libraries.AddRange([autoEncodeLibrary, manualLibrary]);
        await ctx.SaveChangesAsync();

        foreach (int movieId in HeldMovieIds)
            ctx.Movies.Add(
                new Movie
                {
                    Id = movieId,
                    Title = $"Held Movie {movieId}",
                    TitleSort = $"held movie {movieId}",
                    LibraryId = _manualLibraryId,
                }
            );
        await ctx.SaveChangesAsync();

        ctx.Libraries.Add(
            new Library
            {
                Id = _tvLibraryId,
                Title = "Manual Shows",
                Type = "tv",
            }
        );
        ctx.Tvs.Add(
            new Tv
            {
                Id = HeldShowId,
                Title = "Held Show",
                LibraryId = _tvLibraryId,
            }
        );
        ctx.Seasons.Add(
            new Season
            {
                Id = HeldSeasonId,
                TvId = HeldShowId,
                SeasonNumber = 1,
            }
        );
        ctx.Episodes.Add(
            new Episode
            {
                Id = HeldEpisodeId,
                TvId = HeldShowId,
                SeasonId = HeldSeasonId,
                SeasonNumber = 1,
                EpisodeNumber = 1,
            }
        );
        await ctx.SaveChangesAsync();
    }

    private static readonly int[] HeldMovieIds = [910001, 910002, 920001, 920002];
    private const int HeldShowId = 940001;
    private const int HeldSeasonId = 940002;
    private const int HeldEpisodeId = 940003;

    public async Task DisposeAsync()
    {
        await using MediaContext ctx = new();
        await ctx.Episodes.Where(e => e.Id == HeldEpisodeId).ExecuteDeleteAsync();
        await ctx.Seasons.Where(s => s.Id == HeldSeasonId).ExecuteDeleteAsync();
        await ctx.Tvs.Where(t => t.Id == HeldShowId).ExecuteDeleteAsync();
        await ctx.Movies.Where(m => HeldMovieIds.Contains(m.Id)).ExecuteDeleteAsync();
        await ctx
            .Libraries.Where(l =>
                l.Id == _autoEncodeLibraryId || l.Id == _manualLibraryId || l.Id == _tvLibraryId
            )
            .ExecuteDeleteAsync();
    }

    private static List<string> QueryPayloadsContaining(string marker)
    {
        using QueueContext queueCtx = new();
        return
        [
            .. queueCtx
                .QueueJobs.AsNoTracking()
                .Where(j => j.Payload.Contains(marker))
                .Select(j => j.Payload),
        ];
    }

    private static StringContent JsonBody(object obj) =>
        new(JsonSerializer.Serialize(obj), Encoding.UTF8, "application/json");

    [Fact]
    public async Task AddFiles_ManualImport_AlwaysDispatchesVideoEncodeJobPerFile()
    {
        object body = new
        {
            library_id = _manualLibraryId.ToString(),
            folder_id = _folderId.ToString(),
            source_driver_id = (string?)null,
            files = new[]
            {
                new { path = "manual/movie-1.mkv", id = "910001" },
                new { path = "manual/movie-2.mkv", id = "910002" },
            },
        };

        HttpResponseMessage response = await _authed.PostAsync(
            "/api/v1/dashboard/server/addfiles",
            JsonBody(body)
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        List<string> payloads = QueryPayloadsContaining(_manualLibraryId.ToString());

        payloads.Should().HaveCount(2, "one VideoEncodeJob per added file");
        payloads
            .Should()
            .OnlyContain(
                p => p.Contains("VideoEncodeJob"),
                "manual add-files is a deliberate import that always encodes the explicit source "
                    + "file — AutoEncodeOnScan gates only the automatic scan path, not this endpoint"
            );
    }

    [Fact]
    public async Task AddFiles_MovieTheServerDoesNotHold_QueuesAMovieImportInsteadOfAnEncode()
    {
        object body = new
        {
            library_id = _manualLibraryId.ToString(),
            folder_id = _folderId.ToString(),
            source_driver_id = (string?)null,
            files = new[] { new { path = "manual/Example.Movie.2026.mkv", id = "930001" } },
        };

        HttpResponseMessage response = await _authed.PostAsync(
            "/api/v1/dashboard/server/addfiles",
            JsonBody(body)
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        List<string> payloads = QueryPayloadsContaining(_manualLibraryId.ToString());

        payloads.Should().ContainSingle();
        payloads[0].Should().Contain("MovieImportJob").And.Contain("Example.Movie.2026.mkv");
        payloads[0].Should().NotContain("VideoEncodeJob");
    }

    // #656: the picker sends id 0 for a file it found no match for.
    [Theory]
    [InlineData("0")]
    [InlineData("")]
    [InlineData("-5")]
    [InlineData("no-match")]
    public async Task AddFiles_MovieFileWithoutAMatch_IsNotQueuedAndTheMatchedFileStillIs(
        string unmatchedId
    )
    {
        object body = new
        {
            library_id = _manualLibraryId.ToString(),
            folder_id = _folderId.ToString(),
            source_driver_id = (string?)null,
            files = new[]
            {
                new { path = "manual/Held Movie.mkv", id = "910001" },
                new { path = "manual/Unmatched Home Video.mkv", id = unmatchedId },
            },
        };

        HttpResponseMessage response = await _authed.PostAsync(
            "/api/v1/dashboard/server/addfiles",
            JsonBody(body)
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        List<string> payloads = QueryPayloadsContaining(_manualLibraryId.ToString());

        payloads.Should().ContainSingle("only the matched file is queued");
        payloads[0].Should().Contain("VideoEncodeJob").And.Contain("Held Movie.mkv");
        payloads
            .Should()
            .NotContain(
                p => p.Contains("Unmatched Home Video"),
                "a file without a match has no movie to import or encode against"
            );
    }

    [Fact]
    public async Task AddFiles_ShowFileWithoutAMatch_IsNotQueuedAndTheMatchedFileStillIs()
    {
        object body = new
        {
            library_id = _tvLibraryId.ToString(),
            folder_id = _folderId.ToString(),
            source_driver_id = (string?)null,
            files = new[]
            {
                new { path = "manual/Held Show - S01E01.mkv", id = HeldEpisodeId.ToString() },
                new { path = "manual/Unmatched Home Video.mkv", id = "0" },
            },
        };

        HttpResponseMessage response = await _authed.PostAsync(
            "/api/v1/dashboard/server/addfiles",
            JsonBody(body)
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        List<string> payloads = QueryPayloadsContaining(_tvLibraryId.ToString());

        payloads.Should().ContainSingle("only the matched file is queued");
        payloads[0].Should().Contain("VideoEncodeJob").And.Contain("Held Show - S01E01.mkv");
        payloads
            .Should()
            .NotContain(
                p => p.Contains("Unmatched Home Video"),
                "a file without a match has no episode to import or encode against"
            );
    }

    [Fact]
    public async Task AddFiles_TwoFilesResolvedToTheSameEpisode_QueuesOnlyTheFirst()
    {
        // Two encodes on one media id are two ffmpeg runs writing the same
        // output directory, and the one that finishes last wins — the operator
        // gets a file whose name describes the other one. It reached the library
        // as a show's creditless opening and its ending both matching the
        // episode the show actually has, and again as a dual-audio pair.
        //
        // The match is made by the file list and arrives here inside the
        // request, so once a collision is dispatched no parser fix can reach it.
        // The rule is the id, not the name: nothing here knows what NCOP means.
        object body = new
        {
            library_id = _manualLibraryId.ToString(),
            folder_id = _folderId.ToString(),
            source_driver_id = (string?)null,
            // Listed the way the picker actually lists them: sorted by name, so
            // NCED and NCOP both arrive BEFORE the episode they collided with.
            // Keeping "whichever came first" would queue the opening titles and
            // throw away the episode.
            files = new[]
            {
                new { path = "manual/Show - NCED - Ending.mkv", id = "920001" },
                new { path = "manual/Show - NCOP - Opening.mkv", id = "920001" },
                new { path = "manual/Show - S01E01 - Real Episode.mkv", id = "920001" },
                new { path = "manual/Show - S01E02 - Next Episode.mkv", id = "920002" },
            },
        };

        HttpResponseMessage response = await _authed.PostAsync(
            "/api/v1/dashboard/server/addfiles",
            JsonBody(body)
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        List<string> payloads = QueryPayloadsContaining(_manualLibraryId.ToString());

        payloads
            .Should()
            .HaveCount(2, "one encode per episode — four files claimed two episodes between them");
        payloads
            .Should()
            .ContainSingle(
                p => p.Contains("Real Episode"),
                "the file that spells out S01E01 keeps the episode, even though two files that "
                    + "only guessed at it were listed ahead of it"
            );
        payloads
            .Should()
            .ContainSingle(p => p.Contains("Next Episode"), "an uncontested episode is unaffected");
        payloads
            .Should()
            .NotContain(
                p => p.Contains("NCOP") || p.Contains("NCED"),
                "a second file cannot be queued onto an episode another file already claimed"
            );
    }

    [Fact]
    public async Task AddFiles_AutoEncodeOnWithPreset_DispatchesVideoEncodeJobPerFileWithSourceDriverAndPreset()
    {
        Ulid sourceDriverId = Ulid.NewUlid();

        object body = new
        {
            library_id = _autoEncodeLibraryId.ToString(),
            folder_id = _folderId.ToString(),
            source_driver_id = sourceDriverId.ToString(),
            files = new[]
            {
                new { path = "auto/movie-1.mkv", id = "920001" },
                new { path = "auto/movie-2.mkv", id = "920002" },
            },
        };

        HttpResponseMessage response = await _authed.PostAsync(
            "/api/v1/dashboard/server/addfiles",
            JsonBody(body)
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        List<string> payloads = QueryPayloadsContaining(_autoEncodeLibraryId.ToString());

        payloads.Should().HaveCount(2, "one VideoEncodeJob per added file");
        payloads.Should().OnlyContain(p => p.Contains("VideoEncodeJob"));
        payloads
            .Should()
            .OnlyContain(
                p => p.Contains(sourceDriverId.ToString()),
                "SourceDriverId from the request must be preserved, same as the old call"
            );
        payloads
            .Should()
            .OnlyContain(
                p => p.Contains(_presetId.ToString()),
                "the library's EncodePresetId must be carried onto the dispatched job"
            );
    }
}
