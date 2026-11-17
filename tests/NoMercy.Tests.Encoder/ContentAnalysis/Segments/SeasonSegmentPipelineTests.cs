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

using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.Encoder.ContentAnalysis.Segments;
using NoMercy.Encoder.ContentAnalysis.Segments.Probes;
using NoMercy.Storage;
using NoMercy.Tests.Encoder.ContentAnalysis.Segments.Support;
using NoMercy.Tests.Encoder.Storage;

namespace NoMercy.Tests.Encoder.ContentAnalysis.Segments;

/// <summary>
/// The season pipeline drives the FFmpeg passes in the right order and probes
/// exactly what the candidates ask for. A probe that plays back the recorded
/// Hellsing S01 signals stands in for FFmpeg.
/// </summary>
[Trait("Category", "Unit")]
public class SeasonSegmentPipelineTests
{
    private readonly IStorage _storage = TestStorageFactory.CreateLocal();

    [Fact]
    public async Task HellsingS01_ThroughThePipeline_HoldsTheTable_AndProbesOnlyCandidateEdges()
    {
        RecordedSeason season = ResearchCorpus.Committed("hellsing-s01");
        RecordedProbe probe = new(season);
        SeasonSegmentPipeline pipeline = new(
            probe,
            new SeasonSegmentAnalyzer(),
            NullLogger<SeasonSegmentPipeline>.Instance
        );

        IReadOnlyList<EpisodeSegments> result = await pipeline.AnalyseAsync(
            Inputs(season),
            CancellationToken.None
        );

        result.Should().HaveCount(13);
        AcceptanceChecks.Failures(season, result).Should().BeEmpty();
        probe.AudioPasses.Should().Be(13);
        int candidates = result.Sum(e => e.Candidates.Count);
        probe
            .EdgeProbes.Should()
            .BeInRange(1, 2 * candidates, "at most one probe per candidate edge");
        probe.ThumbnailSpans.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task EpisodesWhoseAudioFails_AreLeftOut()
    {
        RecordedSeason season = ResearchCorpus.Committed("hellsing-s01");
        RecordedProbe probe = new(season) { FailAudioFor = season.Episodes[0].Name };
        SeasonSegmentPipeline pipeline = new(
            probe,
            new SeasonSegmentAnalyzer(),
            NullLogger<SeasonSegmentPipeline>.Instance
        );

        IReadOnlyList<EpisodeSegments> result = await pipeline.AnalyseAsync(
            Inputs(season),
            CancellationToken.None
        );

        result.Should().HaveCount(12);
        result.Select(e => e.Key).Should().NotContain(season.Episodes[0].Name);
    }

    private List<SegmentAnalysisInput> Inputs(RecordedSeason season) =>
        [
            .. season.Episodes.Select(e => new SegmentAnalysisInput(
                e.Name,
                new(_storage, e.Name, 0, e.Name)
            )),
        ];

    /// <summary>Plays back recorded signals; counts what was asked.</summary>
    private sealed class RecordedProbe(RecordedSeason season) : IEpisodeSignalProbe
    {
        public int AudioPasses { get; private set; }

        public int EdgeProbes { get; private set; }

        public int ThumbnailSpans { get; private set; }

        public string? FailAudioFor { get; init; }

        public Task<AudioSignals?> ReadAudioAsync(SegmentMediaSource source, CancellationToken ct)
        {
            AudioPasses++;
            if (source.AudioPath == FailAudioFor)
                return Task.FromResult<AudioSignals?>(null);
            EpisodeSignals signals = Episode(source).Signals;
            return Task.FromResult<AudioSignals?>(
                new(signals.DurationSeconds, signals.Fingerprint.Single().Hashes, signals.Silences)
            );
        }

        public Task<BlackRuns?> ReadBlackAsync(
            SegmentMediaSource source,
            IReadOnlyList<TimeRange> windows,
            CancellationToken ct
        ) => Task.FromResult(Episode(source).Signals.Black);

        public Task<OcrScan?> ReadTextAsync(
            SegmentMediaSource source,
            double start,
            CancellationToken ct
        ) => Task.FromResult(Episode(source).Signals.Ocr);

        public Task<EdgeProbe?> ProbeEdgeAsync(
            SegmentMediaSource source,
            double at,
            CancellationToken ct
        )
        {
            EdgeProbes++;
            return Task.FromResult(Episode(source).EdgeProbes?.Nearest(at));
        }

        public Task<ThumbnailStrip?> ReadThumbnailsAsync(
            SegmentMediaSource source,
            TimeRange span,
            CancellationToken ct
        )
        {
            ThumbnailSpans++;
            ThumbnailStrip? recorded = Episode(source).Thumbnails;
            if (recorded is null)
                return Task.FromResult<ThumbnailStrip?>(null);

            List<int> frames =
            [
                .. Enumerable
                    .Range(0, recorded.Count)
                    .Where(i => recorded.Times[i] >= span.Start && recorded.Times[i] <= span.End),
            ];
            return Task.FromResult<ThumbnailStrip?>(
                new(
                    [.. frames.Select(i => recorded.Times[i])],
                    [.. frames.SelectMany(i => recorded.Frame(i).ToArray())]
                )
            );
        }

        private RecordedEpisode Episode(SegmentMediaSource source) =>
            season.Episodes.Single(e => e.Name == source.AudioPath);
    }
}
