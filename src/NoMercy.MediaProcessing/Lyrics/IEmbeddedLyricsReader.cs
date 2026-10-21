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

namespace NoMercy.MediaProcessing.Lyrics;

/// <summary>
/// Reads the lyrics a music file carries in its own tags, through the storage
/// facade, so a caller never handles the file's path on disk itself.
/// </summary>
public interface IEmbeddedLyricsReader
{
    /// <summary>The embedded lyrics, or null when the file has none or cannot be read.</summary>
    Task<EmbeddedLyrics?> ReadAsync(string path, CancellationToken ct = default);
}
