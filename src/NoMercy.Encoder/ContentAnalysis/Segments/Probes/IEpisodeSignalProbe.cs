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

namespace NoMercy.Encoder.ContentAnalysis.Segments.Probes;

/// <summary>
/// The FFmpeg passes of the segment analysis (#598). Each returns null when its
/// pass could not run; the analysis then degrades (no OCR: no text-only credits)
/// instead of guessing. Every pass yields to active playback first.
/// </summary>
public interface IEpisodeSignalProbe
{
    /// <summary>
    /// Stage 1: one pass over the whole audio track, chromaprint (raw) and
    /// silencedetect (-50 dB, 0.3 s) together. No window: openings sit as late
    /// as 14:31.
    /// </summary>
    Task<AudioSignals?> ReadAudioAsync(SegmentMediaSource source, CancellationToken ct);

    /// <summary>blackdetect, strict (pic_th 0.98) and loose (pic_th 0.80), over the given windows.</summary>
    Task<BlackRuns?> ReadBlackAsync(
        SegmentMediaSource source,
        IReadOnlyList<TimeRange> windows,
        CancellationToken ct
    );

    /// <summary>OCR (tesseract eng) at 1 fps from <paramref name="start"/> to the end; null without a model.</summary>
    Task<OcrScan?> ReadTextAsync(SegmentMediaSource source, double start, CancellationToken ct);

    /// <summary>Stage 4: scdet cuts and strict black within ±6 s of <paramref name="at"/>.</summary>
    Task<EdgeProbe?> ProbeEdgeAsync(SegmentMediaSource source, double at, CancellationToken ct);

    /// <summary>Stage 5: 2 frames per second as 32x18 grey over <paramref name="span"/>.</summary>
    Task<ThumbnailStrip?> ReadThumbnailsAsync(
        SegmentMediaSource source,
        TimeRange span,
        CancellationToken ct
    );
}
