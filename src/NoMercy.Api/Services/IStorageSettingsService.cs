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

namespace NoMercy.Api.Services;

public interface IStorageSettingsService
{
    StorageSettings Read();

    /// <summary>Stores every given field, or none when any one is out of range.</summary>
    Task<StorageSettingsChange> ApplyAsync(StorageSettingsPatch patch, Guid userId);
}
