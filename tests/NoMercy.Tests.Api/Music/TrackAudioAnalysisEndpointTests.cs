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
using NoMercy.Database.Models.Music;
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
    private const string SweepRoute = "/api/v1/dashboard/tasks/audio-analysis/sweep";

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

    /// <summary>
    /// The button that says "do it now". Analysis already happens on every
    /// import and once an hour after that, so this is the extra, not the way
    /// in — but it has to actually reach the queue, which is why the assertion
    /// is on the tracks the seeded music library owns rather than on a 200.
    /// </summary>
    [Fact]
    public async Task AudioAnalysisSweep_QueuesTheSeededMusicLibrary()
    {
        HttpResponseMessage response = await _owner.PostAsync(SweepRoute, JsonBody(new { }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("queued").GetInt32().Should().BeGreaterThanOrEqualTo(2);

        await using QueueContext queueContext = new();
        bool queuedAnAnalysisJob = await queueContext
            .QueueJobs.AsNoTracking()
            .AnyAsync(job => job.Payload.Contains("MusicAnalysisJob"));

        queuedAnAnalysisJob.Should().BeTrue();
    }

    [Fact]
    public async Task AudioAnalysisSweep_RejectsAnonymousCallers()
    {
        HttpResponseMessage response = await _anonymous.PostAsync(SweepRoute, JsonBody(new { }));

        response
            .StatusCode.Should()
            .BeOneOf([HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden]);
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

    /// <summary>
    /// The DJ record and stems the automix plugin wrote for a track ride
    /// along with its base analysis — a nested field rather than a second
    /// round trip, since a caller asking for one almost always wants both.
    /// </summary>
    [Fact]
    public async Task Analysis_IncludesTheDjRecordAndStems_WhenPresent()
    {
        Guid trackId = Guid.NewGuid();
        string storageKey = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");

        using (MediaContext context = new())
        {
            context.Tracks.Add(
                new Track
                {
                    Id = trackId,
                    Name = "Dj Endpoint Track",
                    FolderId = NoMercyApiFactory.MusicFolderId,
                }
            );

            context.TrackAudioAnalysis.Add(
                new TrackAudioAnalysis
                {
                    TrackId = trackId,
                    AnalyzerVersion = 1,
                    State = AudioAnalysisState.Ok,
                    KeyName = "Am",
                    KeyCamelot = "8A",
                    AnalyzedAt = DateTime.UtcNow,
                }
            );

            context.TrackDjAnalysis.Add(
                new TrackDjAnalysis
                {
                    TrackId = trackId,
                    ProducerPluginId = Ulid.NewUlid(),
                    DjAnalyzerVersion = 1,
                    BaseAnalyzerVersion = 1,
                    State = AudioAnalysisState.Ok,
                    DownbeatIndex = 0,
                    BeatsPerBar = 4,
                    PhraseLengthBars = 8,
                    PhraseStartsMs = "[0,32000,64000]",
                    VocalRegionsMs = "[[1000,5000],[9000,15000]]",
                    BarEnergy = "[-20.5,-18.2,-14.0]",
                    CuePoints = """[{"ms":1000,"type":"intro","direction":"mixIn","score":0.9}]""",
                    Chords = """[{"ms":0,"chord":"Am"}]""",
                    AnalyzedAt = DateTime.UtcNow,
                }
            );

            context.DerivedAudio.Add(
                new DerivedAudio
                {
                    Key = storageKey,
                    ContentType = "audio/opus",
                    Bytes = 10,
                    CreatedAt = DateTime.UtcNow,
                    LastUsedAt = DateTime.UtcNow,
                }
            );

            context.TrackStems.Add(
                new TrackStem
                {
                    Id = Ulid.NewUlid(),
                    TrackId = trackId,
                    Kind = "vocals",
                    Coverage = StemCoverage.Full,
                    Format = "opus",
                    SampleRate = 48000,
                    StorageKey = storageKey,
                    ProducerVersion = "spleeter-2stems-f16@v1.0.41",
                    CreatedAt = DateTime.UtcNow,
                }
            );

            context.SaveChanges();
        }

        HttpResponseMessage response = await _owner.PostAsync(
            AnalysisRoute,
            JsonBody(new { track_ids = new[] { trackId } })
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement row = doc.RootElement.GetProperty("data")[0];

        JsonElement dj = row.GetProperty("dj");
        dj.GetProperty("dj_analyzer_version").GetInt32().Should().Be(1);
        dj.GetProperty("base_analyzer_version").GetInt32().Should().Be(1);
        dj.GetProperty("downbeat_index").GetInt32().Should().Be(0);
        dj.GetProperty("beats_per_bar").GetInt32().Should().Be(4);
        dj.GetProperty("phrase_length_bars").GetInt32().Should().Be(8);
        dj.GetProperty("phrase_starts_ms")
            .EnumerateArray()
            .Select(e => e.GetInt32())
            .Should()
            .Equal(0, 32000, 64000);

        JsonElement cuePoint = dj.GetProperty("cue_points")[0];
        cuePoint.GetProperty("ms").GetInt32().Should().Be(1000);
        cuePoint.GetProperty("type").GetString().Should().Be("intro");
        cuePoint.GetProperty("direction").GetString().Should().Be("mixIn");
        cuePoint.GetProperty("score").GetDouble().Should().Be(0.9);

        // "Am" -> tonic A -> pitch class 9.
        dj.GetProperty("pitch_class").GetInt32().Should().Be(9);

        JsonElement stems = row.GetProperty("stems");
        stems.GetArrayLength().Should().Be(1);
        stems[0].GetProperty("kind").GetString().Should().Be("vocals");
        stems[0].GetProperty("coverage").GetString().Should().Be("full");
        stems[0].GetProperty("storage_key").GetString().Should().Be(storageKey);
    }

    /// <summary>
    /// Most tracks never get a DJ row or a stem file — the shape has to be
    /// honest about that rather than fake an empty-looking DJ object.
    /// </summary>
    [Fact]
    public async Task Analysis_HasNullDjAndEmptyStems_WhenAbsent()
    {
        Guid trackId = Guid.NewGuid();

        using (MediaContext context = new())
        {
            context.Tracks.Add(
                new Track
                {
                    Id = trackId,
                    Name = "No Dj Endpoint Track",
                    FolderId = NoMercyApiFactory.MusicFolderId,
                }
            );

            context.TrackAudioAnalysis.Add(
                new TrackAudioAnalysis
                {
                    TrackId = trackId,
                    AnalyzerVersion = 1,
                    State = AudioAnalysisState.Ok,
                    AnalyzedAt = DateTime.UtcNow,
                }
            );

            context.SaveChanges();
        }

        HttpResponseMessage response = await _owner.PostAsync(
            AnalysisRoute,
            JsonBody(new { track_ids = new[] { trackId } })
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement row = doc.RootElement.GetProperty("data")[0];

        row.GetProperty("dj").ValueKind.Should().Be(JsonValueKind.Null);
        row.GetProperty("stems").GetArrayLength().Should().Be(0);
    }

    /// <summary>
    /// The dashboard's audio-analysis panel needs the automix side of the
    /// picture too: how much of the library the DJ analyzer has reached, and
    /// how much of the derived-audio store the stems it wrote occupy.
    /// </summary>
    [Fact]
    public async Task AudioAnalysisStatus_ReportsDjCountersAndStemBytes()
    {
        Guid okTrackId = Guid.NewGuid();
        Guid failedTrackId = Guid.NewGuid();
        string storageKey = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        const long seededBytes = 123_456;

        using (MediaContext context = new())
        {
            context.Tracks.AddRange(
                new Track
                {
                    Id = okTrackId,
                    Name = "Dj Status Ok",
                    FolderId = NoMercyApiFactory.MusicFolderId,
                },
                new Track
                {
                    Id = failedTrackId,
                    Name = "Dj Status Failed",
                    FolderId = NoMercyApiFactory.MusicFolderId,
                }
            );

            context.TrackDjAnalysis.AddRange(
                new TrackDjAnalysis
                {
                    TrackId = okTrackId,
                    ProducerPluginId = Ulid.NewUlid(),
                    DjAnalyzerVersion = 1,
                    BaseAnalyzerVersion = 1,
                    State = AudioAnalysisState.Ok,
                    AnalyzedAt = DateTime.UtcNow,
                },
                new TrackDjAnalysis
                {
                    TrackId = failedTrackId,
                    ProducerPluginId = Ulid.NewUlid(),
                    DjAnalyzerVersion = 1,
                    BaseAnalyzerVersion = 1,
                    State = AudioAnalysisState.Failed,
                    FailureReason = "vocal detector produced no regions",
                    AnalyzedAt = DateTime.UtcNow,
                }
            );

            context.DerivedAudio.Add(
                new DerivedAudio
                {
                    Key = storageKey,
                    ContentType = "audio/opus",
                    Bytes = seededBytes,
                    CreatedAt = DateTime.UtcNow,
                    LastUsedAt = DateTime.UtcNow,
                }
            );

            context.SaveChanges();
        }

        HttpResponseMessage response = await _owner.GetAsync(
            "/api/v1/dashboard/tasks/audio-analysis/status"
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("dj_analyzed").GetInt32().Should().BeGreaterThanOrEqualTo(1);
        doc.RootElement.GetProperty("dj_failed").GetInt32().Should().BeGreaterThanOrEqualTo(1);
        doc.RootElement.GetProperty("stems_bytes")
            .GetInt64()
            .Should()
            .BeGreaterThanOrEqualTo(seededBytes);
    }
}
