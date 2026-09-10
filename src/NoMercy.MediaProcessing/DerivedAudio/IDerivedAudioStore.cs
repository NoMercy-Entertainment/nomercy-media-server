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

namespace NoMercy.MediaProcessing.DerivedAudio;

/// <summary>
/// Content-addressed store for rendered audio (stems, rendered transitions):
/// the file lives under <c>AppFiles.DerivedAudioPath</c> keyed by its sha256,
/// and a register row (<c>MediaContext.DerivedAudio</c>) tracks its size and
/// last use for <see cref="EvictAsync"/>.
/// </summary>
public interface IDerivedAudioStore
{
    /// <summary>Streams the content to a temp file while hashing it, then moves it under its key. Same content twice is one file and one row.</summary>
    Task<DerivedAudioEntry> PutAsync(
        Stream content,
        string contentType,
        CancellationToken ct = default
    );

    Task<bool> ExistsAsync(string key, CancellationToken ct = default);

    /// <summary>Null when the key is unknown. Bumps LastUsedAt.</summary>
    Task<Stream?> OpenReadAsync(string key, CancellationToken ct = default);

    Task TouchAsync(string key, CancellationToken ct = default);

    Task DeleteAsync(string key, CancellationToken ct = default);

    /// <summary>Applies the policy: returns how many bytes were freed.</summary>
    Task<long> EvictAsync(long capBytes, TimeSpan grace, CancellationToken ct = default);

    /// <summary>The storage-relative path of a key: "ab/abcdef…". For the audio tools, which hand ffmpeg a local path through a lease.</summary>
    string RelativePath(string key);
}
