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

using NoMercy.Data.Plugins;

namespace NoMercy.Tests.Repositories.Plugins;

/// <summary>A machine without the RoFormer models: every split is Spleeter, as before RoFormer.</summary>
internal sealed class SpleeterOnlySources : IStemsplitSourceSelector
{
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
