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

namespace NoMercy.Plugins.Abstractions;

/// <summary>
/// What ffmpeg reads: a library track by id, or a file in the derived store
/// by key. Exactly one of the two is set, and the factory methods below are
/// the only way to build one, so a caller never has to weigh which id a
/// filter graph is meant to run against.
/// </summary>
/// <param name="TrackId">Set by <see cref="Track" />; null when the input is a derived file.</param>
/// <param name="StorageKey">Set by <see cref="Derived" />; null when the input is a library track.</param>
public sealed record PluginAudioInput(string? TrackId, string? StorageKey)
{
    /// <summary>A track already in the library, addressed by its id.</summary>
    public static PluginAudioInput Track(string trackId) => new(trackId, null);

    /// <summary>
    /// A file already in the derived store - a stem, an earlier render -
    /// addressed by the key <see cref="IPluginDerivedAudio.PutAsync" /> returned.
    /// </summary>
    public static PluginAudioInput Derived(string storageKey) => new(null, storageKey);
}
