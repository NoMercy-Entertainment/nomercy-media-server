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

namespace NoMercy.Data.Plugins;

/// <summary>
/// Always Spleeter: what a host without a <see cref="StemsplitSourceSelector" />
/// splits with, as every host did before RoFormer.
/// </summary>
public sealed class SpleeterOnlySourceSelector : IStemsplitSourceSelector
{
    public static SpleeterOnlySourceSelector Instance { get; } = new();

    public Task<StemsplitSource> ChooseAsync(
        RoformerPreset preset,
        CancellationToken ct = default
    ) => Task.FromResult(StemsplitSource.Spleeter);

    public Task RememberRefusalAsync(
        IReadOnlyCollection<RoformerPreset> presets,
        string refusalLine,
        CancellationToken ct = default
    ) => Task.CompletedTask;
}
