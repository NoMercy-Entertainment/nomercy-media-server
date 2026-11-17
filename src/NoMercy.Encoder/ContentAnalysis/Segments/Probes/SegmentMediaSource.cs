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

using NoMercy.Storage;

namespace NoMercy.Encoder.ContentAnalysis.Segments.Probes;

/// <summary>
/// Where one episode's audio and picture are read from. For an HLS episode the
/// audio and video are separate playlists; for a single file both paths are the
/// file. Paths are addressed against <paramref name="Storage"/>.
/// </summary>
/// <param name="AudioStreamIndex">
/// Which audio stream of <paramref name="AudioPath"/> to analyse (<c>0:a:N</c>).
/// Every episode of a season must use the same language: themes are matched by sound.
/// </param>
/// <param name="AudioLanguage">
/// For a file with several audio streams: the language tag of the stream to
/// read (FFmpeg's <c>0:a:m:language:xxx</c>); <paramref name="AudioStreamIndex"/>
/// is the fallback when no stream carries it. Null reads by index only.
/// </param>
public sealed record SegmentMediaSource(
    IStorage Storage,
    string AudioPath,
    int AudioStreamIndex,
    string VideoPath,
    string? AudioLanguage = null
);
