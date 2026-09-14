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

namespace NoMercy.Data.Repositories;

/// <summary>Reads and writes server-wide settings in the Configuration table.</summary>
public interface IServerConfigurationRepository
{
    Task<string?> GetValueAsync(string key, CancellationToken ct = default);

    /// <summary>The configured server name, or the machine name when none is set.</summary>
    Task<string> GetServerNameAsync(CancellationToken ct = default);

    /// <summary>
    /// Stores <paramref name="value"/> under <paramref name="key"/>. A null
    /// <paramref name="modifiedBy"/> keeps the user who last changed the setting.
    /// </summary>
    Task SetValueAsync(string key, string value, Guid? modifiedBy, CancellationToken ct = default);
}
