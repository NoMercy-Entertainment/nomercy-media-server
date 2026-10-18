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

using NoMercy.Encoder.Analysis;
using NoMercy.NmSystem.Dto;

namespace NoMercy.OpticalMedia.Sources;

public record DiscInfo(
    OpticalDiscType Type,
    string? DiscLabel,
    DiscTitle[] Titles,
    DiscTrack[]? AudioTracks,
    TimeSpan TotalDuration,
    DiscProtection? Protection = null,
    /// <summary>
    /// Human-readable title read from disc-embedded metadata (Blu-ray
    /// <c>BDMV/META/DL/bdmt_*.xml</c>). Preferred over the raw volume
    /// label when available. Null when no embedded title was found.
    /// </summary>
    string? DiscTitle = null,
    string? DrivePath = null
)
{
    /// <summary>
    /// Duration in whole seconds of the main feature title on this disc.
    /// Picks the title flagged <see cref="DiscTitle.IsMainFeature"/>, or the
    /// longest title when none is flagged. Zero when the disc has no titles.
    /// </summary>
    public int MainTitleDurationSec =>
        (
            Titles.FirstOrDefault(t => t.IsMainFeature)
            ?? Titles.OrderByDescending(t => t.Duration).FirstOrDefault()
        )
            ?.Duration
            .TotalSeconds
            is { } secs
            ? (int)secs
            : 0;
}

/// <summary>
/// What's blocking decryption when a probe encountered DRM the host
/// can't handle. Surfaces in the API so the UI can show "this disc
/// is AACS-protected; ensure your KEYDB / drive is compatible" rather
/// than a silent empty title list.
/// </summary>
public record DiscProtection(string Kind, string? VolumeId, string Message);

public record DiscTitle(
    int Index,
    string? Name,
    TimeSpan Duration,
    VideoStreamInfo[] VideoStreams,
    AudioStreamInfo[] AudioStreams,
    SubtitleStreamInfo[] Subtitles,
    ChapterInfo[] Chapters,
    long EstimatedSizeBytes,
    bool IsMainFeature
);

public record DiscTrack(
    int Index,
    string? Title,
    string? Artist,
    TimeSpan Duration,
    int SampleRate,
    int Channels
);
