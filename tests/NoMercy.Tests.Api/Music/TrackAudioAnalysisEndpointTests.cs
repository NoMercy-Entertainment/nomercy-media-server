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
using NoMercy.Tests.Api.Infrastructure;
using Xunit;

namespace NoMercy.Tests.Api.Music;

/// <summary>
/// The analysis read surface, exercised through the real HTTP pipeline rather
/// than by calling the controller method — a route that is never registered
/// answers a direct call perfectly and 404s for a client.
/// </summary>
[Trait("Category", "Characterization")]
public class TrackAudioAnalysisEndpointTests : IClassFixture<NoMercyApiFactory>
{
    private const string AnalysisRoute = "/api/v1/music/tracks/analysis";

    private readonly HttpClient _owner;
    private readonly HttpClient _anonymous;

    public TrackAudioAnalysisEndpointTests(NoMercyApiFactory factory)
    {
        _owner = factory.CreateClient().AsAuthenticated();
        _anonymous = factory.CreateClient().AsUnauthenticated();
    }

    private static StringContent JsonBody(object body) =>
        new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    [Fact]
    public async Task Analysis_RejectsAnonymousCallers()
    {
        HttpResponseMessage response = await _anonymous.PostAsync(
            AnalysisRoute,
            JsonBody(new { track_ids = new[] { Guid.NewGuid() } })
        );

        response
            .StatusCode.Should()
            .BeOneOf([HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden]);
    }

    [Fact]
    public async Task Analysis_IsRoutedAndReturnsAnEmptySetForUnknownTracks()
    {
        HttpResponseMessage response = await _owner.PostAsync(
            AnalysisRoute,
            JsonBody(new { track_ids = new[] { Guid.NewGuid(), Guid.NewGuid() } })
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("data").GetArrayLength().Should().Be(0);
    }

    /// <summary>
    /// Progress needs a real route too. The dashboard reads this to draw a
    /// count rather than a spinner, and it is Moderator-gated like the rest of
    /// the tasks surface.
    /// </summary>
    [Fact]
    public async Task AudioAnalysisStatus_IsRoutedAndReportsCounts()
    {
        HttpResponseMessage response = await _owner.GetAsync(
            "/api/v1/dashboard/tasks/audio-analysis/status"
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("queued").GetInt32().Should().BeGreaterThanOrEqualTo(0);
        doc.RootElement.GetProperty("analyzed").GetInt32().Should().BeGreaterThanOrEqualTo(0);
        doc.RootElement.GetProperty("failed").GetInt32().Should().BeGreaterThanOrEqualTo(0);
        doc.RootElement.TryGetProperty("paused", out _).Should().BeTrue();
    }

    [Fact]
    public async Task AudioAnalysisStatus_RejectsAnonymousCallers()
    {
        HttpResponseMessage response = await _anonymous.GetAsync(
            "/api/v1/dashboard/tasks/audio-analysis/status"
        );

        response
            .StatusCode.Should()
            .BeOneOf([HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden]);
    }

    [Fact]
    public async Task Analysis_RejectsAnEmptyRequest()
    {
        HttpResponseMessage response = await _owner.PostAsync(
            AnalysisRoute,
            JsonBody(new { track_ids = Array.Empty<Guid>() })
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// Without a cap, one request asks the server to materialize an entire
    /// library's analysis in a single hop.
    /// </summary>
    [Fact]
    public async Task Analysis_RejectsAnOversizedRequest()
    {
        Guid[] tooMany = Enumerable.Range(0, 501).Select(_ => Guid.NewGuid()).ToArray();

        HttpResponseMessage response = await _owner.PostAsync(
            AnalysisRoute,
            JsonBody(new { track_ids = tooMany })
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
