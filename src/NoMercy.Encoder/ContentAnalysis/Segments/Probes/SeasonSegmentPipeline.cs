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

using Microsoft.Extensions.Logging;

namespace NoMercy.Encoder.ContentAnalysis.Segments.Probes;

/// <summary>
/// Runs the #598 analysis for one season: per episode the audio pass, the
/// blackdetect windows and targeted OCR (stage 1 and 7 inputs), then the
/// matching and theme candidates, then exactly the picture probes those
/// candidates ask for (stages 4–5), then the decision.
/// </summary>
public sealed class SeasonSegmentPipeline(
    IEpisodeSignalProbe probe,
    ISeasonSegmentAnalyzer analyzer,
    ILogger<SeasonSegmentPipeline> logger
) : ISeasonSegmentPipeline
{
    public async Task<IReadOnlyList<EpisodeSegments>> AnalyseAsync(
        IReadOnlyList<SegmentAnalysisInput> episodes,
        CancellationToken ct
    )
    {
        List<SegmentAnalysisInput> measured = [];
        List<EpisodeSignals> signals = [];
        foreach (SegmentAnalysisInput episode in episodes)
        {
            ct.ThrowIfCancellationRequested();
            EpisodeSignals? signal = await MeasureAsync(episode, ct);
            if (signal is null)
                continue;
            measured.Add(episode);
            signals.Add(signal);
        }

        if (signals.Count == 0)
            return [];

        IReadOnlyList<PairMatches> matches =
            signals.Count >= SegmentRules.MinEpisodesForThemes ? analyzer.Match(signals, ct) : [];
        SeasonCandidates candidates = analyzer.FindCandidates(signals, matches);

        SeasonEvidence evidence = new(signals.Count);
        for (int i = 0; i < signals.Count; i++)
        {
            SegmentMediaSource source = measured[i].Source;
            EdgeProbeSet probes = new();
            foreach (double at in candidates.EdgeProbeTimes(i))
            {
                EdgeProbe? edge = await probe.ProbeEdgeAsync(source, at, ct);
                if (edge is not null)
                    probes.Add(at, edge);
            }

            List<ThumbnailStrip> strips = [];
            foreach (TimeRange span in candidates.ThumbnailSpans(i))
            {
                ThumbnailStrip? strip = await probe.ReadThumbnailsAsync(source, span, ct);
                if (strip is not null)
                    strips.Add(strip);
            }

            evidence.EdgeProbes[i] = probes;
            evidence.Thumbnails[i] = strips.Count > 0 ? ThumbnailStrip.Merge(strips) : null;
        }

        IReadOnlyList<EpisodeSegments> result = analyzer.Decide(candidates, evidence);
        logger.LogInformation(
            "Segment analysis: {Episodes} episode(s), {Intros} intro(s), {Credits} credits",
            result.Count,
            result.Count(r => r.Intro is not null),
            result.Count(r => r.CreditsStart is not null)
        );
        return result;
    }

    private async Task<EpisodeSignals?> MeasureAsync(SegmentAnalysisInput episode, CancellationToken ct)
    {
        AudioSignals? audio = await probe.ReadAudioAsync(episode.Source, ct);
        if (audio is null)
        {
            logger.LogWarning("Segment analysis: no audio signals for {Key}, left out", episode.Key);
            return null;
        }

        BlackRuns? black = await probe.ReadBlackAsync(
            episode.Source,
            SignalWindows.Black(audio.DurationSeconds, audio.Silences),
            ct
        );
        OcrScan? ocr = await probe.ReadTextAsync(
            episode.Source,
            SignalWindows.OcrStart(audio.DurationSeconds, black),
            ct
        );

        return new(
            episode.Key,
            audio.DurationSeconds,
            [new FingerprintPart(0, audio.Hashes)],
            audio.Silences,
            black,
            ocr
        );
    }
}
