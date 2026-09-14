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

namespace NoMercy.Data.Repositories;

public interface IEncodingPresetRepository
{
    Task<List<EncodingPreset>> ListAsync(
        int pageSize = 100,
        int pageIndex = 0,
        string? tagFilter = null
    );

    Task<IReadOnlyList<string>> GetAllTagsAsync();

    Task<EncodingPreset?> GetByIdAsync(Ulid id);

    Task<EncodingPreset?> GetByNameAsync(string name);

    Task<EncodingPreset> CreateAsync(EncodingPreset preset);

    Task<EncodingPreset?> UpdateAsync(Ulid id, Action<EncodingPreset> apply);

    Task<bool> DeleteAsync(Ulid id);

    /// <summary>Whether any preset inherits from the preset with this id.</summary>
    Task<bool> HasChildrenAsync(Ulid id, CancellationToken ct = default);

    Task<int> GetTotalCountAsync();
}
