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
/// How many stems Spleeter separates a track into, passed to
/// <see cref="IPluginAudioTools.SplitStemsAsync" />. Named by the value
/// rather than by what each stem is called, because that is what Spleeter's
/// own model names call it and a plugin author picking one is choosing a
/// model, not a track layout.
/// </summary>
public enum PluginStemSet
{
    /// <summary>Vocals and accompaniment.</summary>
    Two = 2,

    /// <summary>Vocals, drums, bass and other.</summary>
    Four = 4,
}
