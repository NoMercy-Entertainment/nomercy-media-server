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

using NoMercy.Database.Models.Media;
using NoMercy.Encoder.Profiles;

namespace NoMercy.Data.Repositories;

/// <summary>
/// Lets <see cref="INamePresetResolver"/> walk a preset's parent chain through the
/// repository, one lookup per ancestor. The resolver is synchronous, so this blocks
/// on the async repository calls; the chain is short.
/// </summary>
public sealed class RepositoryNamePresetLookup(IEncodingPresetRepository repository)
    : INamePresetLookup
{
    public PresetResolveRequest? FindByName(string name)
    {
        EncodingPreset? preset = repository.GetByNameAsync(name).GetAwaiter().GetResult();
        if (preset is null)
            return null;

        string? parentName = preset.ParentPresetId is Ulid parentId
            ? repository.GetByIdAsync(parentId).GetAwaiter().GetResult()?.Name
            : null;

        return new(preset.Name, preset.ProfileJson, parentName);
    }
}
