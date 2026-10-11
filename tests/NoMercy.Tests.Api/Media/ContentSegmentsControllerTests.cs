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
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NoMercy.Database;
using NoMercy.Database.Models.Media;
using NoMercy.Tests.Api.Infrastructure;
using Xunit;

namespace NoMercy.Tests.Api.Media;

/// <summary>
/// Contract tests for <c>api/v1/content-segments</c> — the dashboard/player
/// surface for intro/outro/recap/credits timeline annotations. Locks the
/// Moderator-vs-MediaAccess auth split, the paginated-list envelope
/// (including the pageSize clamp), and the Create validation rules.
/// </summary>
[Trait("Category", "ContentSegments")]
public class ContentSegmentsControllerTests : IClassFixture<NoMercyApiFactory>, IAsyncLifetime
{
    private readonly HttpClient _authed;
    private readonly HttpClient _unauthed;

    // Seeded by NoMercyApiFactory.SeedMediaData.
    private const int SeededMovieId = 129;
    private const int SeededEpisodeId = 62085;

    private Ulid _introSegmentId;
    private Ulid _outroSegmentId;
    private Ulid _episodeSegmentId;

    public ContentSegmentsControllerTests(NoMercyApiFactory factory)
    {
        _authed = factory.CreateClient().AsAuthenticated();
        _unauthed = factory.CreateClient().AsUnauthenticated();
    }

    public async Task InitializeAsync()
    {
        _introSegmentId = Ulid.NewUlid();
        _outroSegmentId = Ulid.NewUlid();
        _episodeSegmentId = Ulid.NewUlid();

        await using MediaContext ctx = new();
        ctx.ContentSegments.AddRange([
            new ContentSegment
            {
                Id = _introSegmentId,
                MovieId = SeededMovieId,
                SegmentType = ContentSegmentType.Intro,
                StartSeconds = 0,
                EndSeconds = 90,
                Source = "detector",
            },
            new ContentSegment
            {
                Id = _outroSegmentId,
                MovieId = SeededMovieId,
                SegmentType = ContentSegmentType.Outro,
                StartSeconds = 5400,
                EndSeconds = 5460,
                Source = "detector",
            },
            new ContentSegment
            {
                Id = _episodeSegmentId,
                EpisodeId = SeededEpisodeId,
                SegmentType = ContentSegmentType.Intro,
                StartSeconds = 5,
                EndSeconds = 35,
                Source = "detector",
            },
        ]);
        await ctx.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await using MediaContext ctx = new();
        Ulid[] ids = [_introSegmentId, _outroSegmentId, _episodeSegmentId];
        await ctx.ContentSegments.Where(s => ids.Contains(s.Id)).ExecuteDeleteAsync();
    }

    [Fact]
    public async Task List_ReturnsUnauthorized_WhenAnonymous()
    {
        HttpResponseMessage response = await _unauthed.GetAsync("/api/v1/content-segments");

        response
            .StatusCode.Should()
            .BeOneOf([HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden]);
    }

    [Fact]
    public async Task List_ReturnsEnvelopeWithDataAndMeta_WhenModerator()
    {
        HttpResponseMessage response = await _authed.GetAsync("/api/v1/content-segments");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        root.TryGetProperty("data", out JsonElement data).Should().BeTrue();
        data.ValueKind.Should().Be(JsonValueKind.Array);

        root.TryGetProperty("meta", out JsonElement meta).Should().BeTrue();
        meta.TryGetProperty("total", out _).Should().BeTrue();
        meta.TryGetProperty("pageSize", out _).Should().BeTrue();
        meta.TryGetProperty("pageIndex", out _).Should().BeTrue();
        meta.TryGetProperty("totalPages", out _).Should().BeTrue();

        bool containsSeededIntro = data.EnumerateArray()
            .Any(e => e.GetProperty("id").GetString() == _introSegmentId.ToString());
        containsSeededIntro.Should().BeTrue("the seeded intro segment must be visible in the list");
    }

    [Fact]
    public async Task List_PageSizeAboveMax_IsClampedTo500()
    {
        HttpResponseMessage response = await _authed.GetAsync(
            "/api/v1/content-segments?pageSize=999999"
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);

        doc.RootElement.GetProperty("meta").GetProperty("pageSize").GetInt32().Should().Be(500);
    }

    [Fact]
    public async Task List_PageSizeBelowMin_IsClampedTo1()
    {
        HttpResponseMessage response = await _authed.GetAsync(
            "/api/v1/content-segments?pageSize=0"
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);

        doc.RootElement.GetProperty("meta").GetProperty("pageSize").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task List_FilteredByType_OnlyReturnsThatType()
    {
        HttpResponseMessage response = await _authed.GetAsync(
            "/api/v1/content-segments?type=Outro&pageSize=500"
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement data = doc.RootElement.GetProperty("data");

        data.GetArrayLength().Should().BeGreaterThan(0);
        foreach (JsonElement item in data.EnumerateArray())
            item.GetProperty("segment_type").GetString().Should().Be("Outro");

        bool containsSeededOutro = data.EnumerateArray()
            .Any(e => e.GetProperty("id").GetString() == _outroSegmentId.ToString());
        containsSeededOutro.Should().BeTrue();
    }

    [Fact]
    public async Task List_FilteredByType_CountsOnlyMatchingSegments()
    {
        List<ContentSegment> extraSegments = [];
        for (int index = 0; index < 7; index++)
        {
            extraSegments.Add(
                new ContentSegment
                {
                    Id = Ulid.NewUlid(),
                    MovieId = SeededMovieId,
                    SegmentType = index == 0 ? ContentSegmentType.Intro : ContentSegmentType.Outro,
                    StartSeconds = 100 + index * 10,
                    EndSeconds = 105 + index * 10,
                    Source = "detector",
                }
            );
        }

        await using (MediaContext setup = new())
        {
            setup.ContentSegments.AddRange(extraSegments);
            await setup.SaveChangesAsync();
        }

        try
        {
            HttpResponseMessage response = await _authed.GetAsync(
                "/api/v1/content-segments?type=Intro&pageSize=2"
            );

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            JsonElement root = doc.RootElement;
            root.GetProperty("data").GetArrayLength().Should().Be(2);
            JsonElement meta = root.GetProperty("meta");
            meta.GetProperty("total").GetInt32().Should().Be(3);
            meta.GetProperty("totalPages").GetInt32().Should().Be(2);

            HttpResponseMessage unfiltered = await _authed.GetAsync(
                "/api/v1/content-segments?pageSize=2"
            );
            unfiltered.StatusCode.Should().Be(HttpStatusCode.OK);
            using JsonDocument unfilteredDoc = JsonDocument.Parse(
                await unfiltered.Content.ReadAsStringAsync()
            );
            unfilteredDoc
                .RootElement.GetProperty("meta")
                .GetProperty("total")
                .GetInt32()
                .Should()
                .Be(10);
        }
        finally
        {
            await using MediaContext cleanup = new();
            Ulid[] ids = extraSegments.Select(segment => segment.Id).ToArray();
            await cleanup
                .ContentSegments.Where(segment => ids.Contains(segment.Id))
                .ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task GetByEpisode_ReturnsUnauthorized_WhenAnonymous()
    {
        HttpResponseMessage response = await _unauthed.GetAsync(
            $"/api/v1/content-segments/episode/{SeededEpisodeId}"
        );

        response
            .StatusCode.Should()
            .BeOneOf([HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden]);
    }

    [Fact]
    public async Task GetByEpisode_ReturnsSeededSegment_WhenAuthenticated()
    {
        HttpResponseMessage response = await _authed.GetAsync(
            $"/api/v1/content-segments/episode/{SeededEpisodeId}"
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement data = doc.RootElement.GetProperty("data");

        data.ValueKind.Should().Be(JsonValueKind.Array);
        data.GetArrayLength().Should().Be(1);

        JsonElement segment = data[0];
        segment.GetProperty("id").GetString().Should().Be(_episodeSegmentId.ToString());
        segment.GetProperty("episode_id").GetInt32().Should().Be(SeededEpisodeId);
        segment.GetProperty("segment_type").GetString().Should().Be("Intro");
    }

    [Fact]
    public async Task GetByMovie_ReturnsUnauthorized_WhenAnonymous()
    {
        HttpResponseMessage response = await _unauthed.GetAsync(
            $"/api/v1/content-segments/movie/{SeededMovieId}"
        );

        response
            .StatusCode.Should()
            .BeOneOf([HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden]);
    }

    [Fact]
    public async Task GetByMovie_ReturnsSeededSegmentsOrderedByStart_WhenAuthenticated()
    {
        HttpResponseMessage response = await _authed.GetAsync(
            $"/api/v1/content-segments/movie/{SeededMovieId}"
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement data = doc.RootElement.GetProperty("data");

        data.GetArrayLength().Should().Be(2, "movie 129 was seeded with an intro and an outro");

        JsonElement first = data[0];
        JsonElement second = data[1];
        first.GetProperty("id").GetString().Should().Be(_introSegmentId.ToString());
        second.GetProperty("id").GetString().Should().Be(_outroSegmentId.ToString());
        first
            .GetProperty("start_seconds")
            .GetDouble()
            .Should()
            .BeLessThan(second.GetProperty("start_seconds").GetDouble());
    }

    [Fact]
    public async Task Create_ReturnsUnauthorized_WhenAnonymous()
    {
        object payload = new
        {
            segment_type = "Recap",
            start_seconds = 10.0,
            end_seconds = 20.0,
            movie_id = SeededMovieId,
        };

        HttpResponseMessage response = await _unauthed.PostAsJsonAsync(
            "/api/v1/content-segments",
            payload
        );

        response
            .StatusCode.Should()
            .BeOneOf([HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden]);
    }

    [Fact]
    public async Task Create_Valid_PersistsAndReturnsSegment_WhenModerator()
    {
        object payload = new
        {
            segment_type = "Recap",
            start_seconds = 100.0,
            end_seconds = 130.0,
            movie_id = SeededMovieId,
        };

        HttpResponseMessage response = await _authed.PostAsJsonAsync(
            "/api/v1/content-segments",
            payload
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        root.GetProperty("segment_type").GetString().Should().Be("Recap");
        root.GetProperty("movie_id").GetInt32().Should().Be(SeededMovieId);
        root.GetProperty("source").GetString().Should().Be("manual");
        Ulid createdId = Ulid.Parse(root.GetProperty("id").GetString()!);

        try
        {
            await using MediaContext ctx = new();
            ContentSegment? persisted = await ctx
                .ContentSegments.AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == createdId);
            persisted.Should().NotBeNull();
            persisted!.StartSeconds.Should().Be(100.0);
            persisted.EndSeconds.Should().Be(130.0);
        }
        finally
        {
            await using MediaContext cleanup = new();
            await cleanup.ContentSegments.Where(s => s.Id == createdId).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task Create_EndBeforeStart_Returns400()
    {
        object payload = new
        {
            segment_type = "Intro",
            start_seconds = 50.0,
            end_seconds = 10.0,
            movie_id = SeededMovieId,
        };

        HttpResponseMessage response = await _authed.PostAsJsonAsync(
            "/api/v1/content-segments",
            payload
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_MissingBothEpisodeAndMovieId_Returns400()
    {
        object payload = new
        {
            segment_type = "Intro",
            start_seconds = 0.0,
            end_seconds = 10.0,
        };

        HttpResponseMessage response = await _authed.PostAsJsonAsync(
            "/api/v1/content-segments",
            payload
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_BothEpisodeAndMovieIdSet_Returns400()
    {
        object payload = new
        {
            segment_type = "Intro",
            start_seconds = 0.0,
            end_seconds = 10.0,
            movie_id = SeededMovieId,
            episode_id = SeededEpisodeId,
        };

        HttpResponseMessage response = await _authed.PostAsJsonAsync(
            "/api/v1/content-segments",
            payload
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Update_ReturnsUnauthorized_WhenAnonymous()
    {
        object payload = new { start_seconds = 1.0 };

        HttpResponseMessage response = await _unauthed.PutAsJsonAsync(
            $"/api/v1/content-segments/{_introSegmentId}",
            payload
        );

        response
            .StatusCode.Should()
            .BeOneOf([HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden]);
    }

    [Fact]
    public async Task Update_Valid_FlipsSourceToManual_WhenModerator()
    {
        object payload = new { start_seconds = 2.0, end_seconds = 95.0 };

        HttpResponseMessage response = await _authed.PutAsJsonAsync(
            $"/api/v1/content-segments/{_introSegmentId}",
            payload
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        root.GetProperty("start_seconds").GetDouble().Should().Be(2.0);
        root.GetProperty("end_seconds").GetDouble().Should().Be(95.0);
        root.GetProperty("source").GetString().Should().Be("manual");

        await using MediaContext ctx = new();
        ContentSegment? persisted = await ctx
            .ContentSegments.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == _introSegmentId);
        persisted.Should().NotBeNull();
        persisted!.Source.Should().Be("manual");
    }

    [Fact]
    public async Task Update_UnknownId_Returns404()
    {
        object payload = new { start_seconds = 1.0 };
        Ulid unknownId = Ulid.NewUlid();

        HttpResponseMessage response = await _authed.PutAsJsonAsync(
            $"/api/v1/content-segments/{unknownId}",
            payload
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Update_InvalidId_Returns400()
    {
        object payload = new { start_seconds = 1.0 };

        HttpResponseMessage response = await _authed.PutAsJsonAsync(
            "/api/v1/content-segments/not-a-valid-ulid",
            payload
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Delete_ReturnsUnauthorized_WhenAnonymous()
    {
        HttpResponseMessage response = await _unauthed.DeleteAsync(
            $"/api/v1/content-segments/{_outroSegmentId}"
        );

        response
            .StatusCode.Should()
            .BeOneOf([HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden]);
    }

    [Fact]
    public async Task Delete_Existing_Returns204AndRemovesRow_WhenModerator()
    {
        HttpResponseMessage response = await _authed.DeleteAsync(
            $"/api/v1/content-segments/{_outroSegmentId}"
        );

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        await using MediaContext ctx = new();
        bool stillExists = await ctx.ContentSegments.AnyAsync(s => s.Id == _outroSegmentId);
        stillExists.Should().BeFalse();
    }

    [Fact]
    public async Task Delete_UnknownId_Returns404()
    {
        Ulid unknownId = Ulid.NewUlid();

        HttpResponseMessage response = await _authed.DeleteAsync(
            $"/api/v1/content-segments/{unknownId}"
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Delete_InvalidId_Returns400()
    {
        HttpResponseMessage response = await _authed.DeleteAsync(
            "/api/v1/content-segments/not-a-valid-ulid"
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
