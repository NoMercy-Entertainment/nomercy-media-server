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

/// <param name="Problem">Why nothing was stored; null when the change was applied.</param>
/// <param name="RestartRequired">The hours and the backup count are read once at start.</param>
public sealed record StorageSettingsChange(
    StorageSettings? Settings,
    string? Problem,
    bool RestartRequired
);
