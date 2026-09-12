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

    /// <summary>
    /// Bumps <c>LastUsedAt</c> and answers whether the key survived to be
    /// bumped: true only when the register row was updated under the key's own
    /// lock AND the content file is there. False is the whole answer a caller
    /// about to hand the path to ffmpeg needs - the key is not one this store
    /// holds, or eviction took it.
    /// <para>
    /// A true also buys the caller the rest of the eviction grace window: the
    /// touch moved <c>LastUsedAt</c> under the same lock eviction re-checks
    /// under, so the sweep leaves the key alone while the caller reads it.
    /// </para>
    /// </summary>
    Task<bool> TouchAsync(string key, CancellationToken ct = default);

    Task DeleteAsync(string key, CancellationToken ct = default);

    /// <summary>Applies the policy: returns how many bytes were freed.</summary>
    Task<long> EvictAsync(long capBytes, TimeSpan grace, CancellationToken ct = default);

    /// <summary>The storage-relative path of a key: "ab/abcdef…". For the audio tools, which hand ffmpeg a local path through a lease.</summary>
    string RelativePath(string key);
}
