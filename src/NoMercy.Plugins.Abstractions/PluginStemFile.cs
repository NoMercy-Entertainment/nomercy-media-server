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

/// <summary>One separated stem, already written to the derived store.</summary>
/// <param name="Kind">
/// What Spleeter named it: "vocals" and "accompaniment" for
/// <see cref="PluginStemSet.Two" />; "vocals", "drums", "bass" and "other"
/// for <see cref="PluginStemSet.Four" />.
/// </param>
/// <param name="Coverage">How much of the track this stem was split from; see <see cref="PluginStemCoverage" />.</param>
/// <param name="StorageKey">The key to read it back with <see cref="IPluginDerivedAudio.OpenReadAsync" />.</param>
/// <param name="Bytes">The stem file's size, for a caller budgeting cache space before it reads anything.</param>
public sealed record PluginStemFile(
    string Kind,
    PluginStemCoverage Coverage,
    string StorageKey,
    long Bytes
);
