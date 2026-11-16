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

using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.Data.Plugins;

/// <summary>
/// What the two source tokens stand for in one run:
/// <see cref="PluginFilterGraphTokens.StemsSource" /> and
/// <see cref="PluginFilterGraphTokens.StemsVocalSource" />.
/// </summary>
internal sealed record StemsplitSources(StemsplitSource Full, StemsplitSource Vocal)
{
    /// <summary>Both tokens as Spleeter: the fall back, and every run that cannot use RoFormer.</summary>
    public static StemsplitSources Spleeter =>
        new(StemsplitSource.Spleeter, StemsplitSource.Spleeter);

    /// <summary>The RoFormer presets this run asks the filter for; empty when it asks for none.</summary>
    public IReadOnlyList<RoformerPreset> RoformerPresets(string graphText)
    {
        List<RoformerPreset> presets = [];

        if (
            Full.Engine == StemsplitEngine.Roformer
            && graphText.Contains(PluginFilterGraphTokens.StemsSource, StringComparison.Ordinal)
        )
            presets.Add(RoformerPreset.Full);

        if (
            Vocal.Engine == StemsplitEngine.Roformer
            && graphText.Contains(
                PluginFilterGraphTokens.StemsVocalSource,
                StringComparison.Ordinal
            )
        )
            presets.Add(RoformerPreset.VocalFull);

        return presets;
    }
}
