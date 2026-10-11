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
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NoMercy.Database;
using NoMercy.Database.Models.Encoder;
using NoMercy.Tests.Api.Infrastructure;
using Xunit;

namespace NoMercy.Tests.Api.Dashboard;

[Trait("Category", "IncompleteEncodes")]
public class IncompleteEncodesControllerTests : IClassFixture<NoMercyApiFactory>, IAsyncLifetime
{
    private readonly HttpClient _authed;

    // IDs inserted per-test and cleaned up in DisposeAsync.
    private int _rowId1;
    private int _rowId2;

    public IncompleteEncodesControllerTests(NoMercyApiFactory factory)
    {
        _authed = factory.CreateClient().AsAuthenticated();
    }

    public async Task InitializeAsync()
    {
        await using MediaContext ctx = new();

        IncompleteEncode row1 = new()
        {
            MediaId = 129,
            FolderId = NoMercyApiFactory.MovieFolderId.ToString(),
            Title = "Spirited Away",
            MissingRenditions = "video/h264/1080p\nvideo/h264/720p",
            LastError = "Finalize timed out",
            AttemptsMade = 1,
            FirstSeenAt = DateTime.UtcNow.AddHours(-2),
            LastSeenAt = DateTime.UtcNow.AddMinutes(-5),
        };
        IncompleteEncode row2 = new()
        {
            MediaId = 680,
            FolderId = NoMercyApiFactory.MovieFolderId.ToString(),
            Title = "Pulp Fiction",
            MissingRenditions = "audio/aac/stereo",
            LastError = null,
            AttemptsMade = 0,
            FirstSeenAt = DateTime.UtcNow.AddHours(-1),
            LastSeenAt = DateTime.UtcNow.AddMinutes(-1),
        };

        ctx.IncompleteEncodes.AddRange(row1, row2);
        await ctx.SaveChangesAsync();

        _rowId1 = row1.Id;
        _rowId2 = row2.Id;
    }

    public async Task DisposeAsync()
    {
        await using MediaContext ctx = new();
        await ctx
            .IncompleteEncodes.Where(r => r.Id == _rowId1 || r.Id == _rowId2)
            .ExecuteDeleteAsync();
    }

    // ── GET /api/v1/dashboard/tasks/queue/incomplete ──────────────────────

    [Fact]
    public async Task List_ReturnsSeededRows()
    {
        HttpResponseMessage response = await _authed.GetAsync(
            "/api/v1/dashboard/tasks/queue/incomplete"
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        string json = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement data = doc.RootElement.GetProperty("data");

        data.GetArrayLength().Should().BeGreaterThanOrEqualTo(2);

        // Find our two rows by title.
        JsonElement[] rows = data.EnumerateArray().ToArray();

        JsonElement? spirited = rows.FirstOrDefault(e =>
            e.GetProperty("title").GetString() == "Spirited Away"
        );
        spirited.Should().NotBeNull("Spirited Away row must appear in the list");

        string[]? renditions = spirited!
            .Value.GetProperty("missing_renditions")
            .EnumerateArray()
            .Select(e => e.GetString()!)
            .ToArray();

        renditions.Should().BeEquivalentTo(["video/h264/1080p", "video/h264/720p"]);
        spirited!.Value.GetProperty("last_error").GetString().Should().Be("Finalize timed out");
        spirited!.Value.GetProperty("attempts_made").GetInt32().Should().Be(1);
    }

    // ── POST /api/v1/dashboard/tasks/queue/incomplete/{id}/retry ─────────

    [Fact]
    public async Task Retry_MissingId_Returns404()
    {
        HttpResponseMessage response = await _authed.PostAsync(
            "/api/v1/dashboard/tasks/queue/incomplete/99999/retry",
            content: null
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Retry_MissingVideoFile_KeepsRowAndReportsFailure()
    {
        await using (MediaContext ctx = new())
        {
            IncompleteEncode row = await ctx.IncompleteEncodes.SingleAsync(r => r.Id == _rowId1);
            row.MediaId = int.MaxValue;
            await ctx.SaveChangesAsync();
        }

        HttpResponseMessage response = await _authed.PostAsync(
            $"/api/v1/dashboard/tasks/queue/incomplete/{_rowId1}/retry",
            content: null
        );

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        string json = await response.Content.ReadAsStringAsync();
        json.Should().NotContain("Re-queued");

        await using MediaContext verificationContext = new();
        (await verificationContext.IncompleteEncodes.AnyAsync(r => r.Id == _rowId1))
            .Should()
            .BeTrue("an undispatched encode must remain available for retry");
    }

    [Theory]
    [InlineData("missing-folder")]
    [InlineData("invalid-folder")]
    [InlineData("overflow-media-id")]
    public async Task Retry_UnresolvableSource_KeepsRowAndReportsFailure(string sourceProblem)
    {
        await using (MediaContext ctx = new())
        {
            IncompleteEncode row = await ctx.IncompleteEncodes.SingleAsync(r => r.Id == _rowId1);
            if (sourceProblem == "missing-folder")
                row.FolderId = Ulid.NewUlid().ToString();
            else if (sourceProblem == "invalid-folder")
                row.FolderId = "invalid";
            else
                row.MediaId = (long)int.MaxValue + 1;
            await ctx.SaveChangesAsync();
        }

        HttpResponseMessage response = await _authed.PostAsync(
            $"/api/v1/dashboard/tasks/queue/incomplete/{_rowId1}/retry",
            content: null
        );

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        string json = await response.Content.ReadAsStringAsync();
        json.Should().NotContain("Re-queued");

        await using MediaContext verificationContext = new();
        (await verificationContext.IncompleteEncodes.AnyAsync(r => r.Id == _rowId1))
            .Should()
            .BeTrue("a row must remain when its source cannot be resolved");
    }

    // ── DELETE /api/v1/dashboard/tasks/queue/incomplete/{id} ──────────────

    [Fact]
    public async Task Delete_MissingId_Returns404()
    {
        HttpResponseMessage response = await _authed.DeleteAsync(
            "/api/v1/dashboard/tasks/queue/incomplete/99999"
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Delete_ExistingRow_RemovesOnlyThatRow()
    {
        HttpResponseMessage response = await _authed.DeleteAsync(
            $"/api/v1/dashboard/tasks/queue/incomplete/{_rowId1}"
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        string json = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("status").GetString().Should().Be("success");

        await using MediaContext ctx = new();
        (await ctx.IncompleteEncodes.AnyAsync(r => r.Id == _rowId1))
            .Should()
            .BeFalse("the deleted row must no longer exist");
        (await ctx.IncompleteEncodes.AnyAsync(r => r.Id == _rowId2))
            .Should()
            .BeTrue("a single-row delete must not touch other quarantine rows");
    }

    // ── DELETE /api/v1/dashboard/tasks/queue/incomplete ────────────────────

    [Fact]
    public async Task DeleteAll_RemovesSeededRows_AndReportsRemovedCount()
    {
        HttpResponseMessage response = await _authed.DeleteAsync(
            "/api/v1/dashboard/tasks/queue/incomplete"
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        string json = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("status").GetString().Should().Be("success");

        // Other tests/processes may hold unrelated quarantine rows in the shared
        // test database, so the count is only asserted as a lower bound — the
        // two rows this test seeded must be included in it.
        doc.RootElement.GetProperty("data").GetInt32().Should().BeGreaterThanOrEqualTo(2);

        await using MediaContext ctx = new();
        (await ctx.IncompleteEncodes.AnyAsync(r => r.Id == _rowId1 || r.Id == _rowId2))
            .Should()
            .BeFalse("both seeded rows must be gone after deleting all incomplete encodes");
    }
}
