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

namespace NoMercy.PluginSdk.Abstractions;

public interface IPluginConfiguration
{
    T? GetConfiguration<T>()
        where T : class, new();
    Task<T?> GetConfigurationAsync<T>(CancellationToken ct = default)
        where T : class, new();
    void SaveConfiguration<T>(T configuration)
        where T : class;
    Task SaveConfigurationAsync<T>(T configuration, CancellationToken ct = default)
        where T : class;
    bool HasConfiguration();
    void DeleteConfiguration();
}
