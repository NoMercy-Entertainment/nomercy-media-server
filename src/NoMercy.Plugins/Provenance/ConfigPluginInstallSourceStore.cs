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

namespace NoMercy.PluginSdk.Provenance;

/// <summary>Install sources in the platform-scoped config, the file consent and grants live in.</summary>
public class ConfigPluginInstallSourceStore(IPluginConfiguration configuration)
    : IPluginInstallSourceStore
{
    private readonly Lock _gate = new();

    public PluginInstallSource? Find(Ulid pluginId)
    {
        PluginInstallSourceRecord? record =
            configuration.GetConfiguration<PluginInstallSourceRecord>();

        return record?.InstallSources.GetValueOrDefault(Key(pluginId));
    }

    public void Record(Ulid pluginId, PluginInstallSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        lock (_gate)
        {
            PluginInstallSourceRecord record =
                configuration.GetConfiguration<PluginInstallSourceRecord>() ?? new();

            record.InstallSources[Key(pluginId)] = source;
            configuration.SaveConfiguration(record);
        }
    }

    public void Forget(Ulid pluginId)
    {
        lock (_gate)
        {
            PluginInstallSourceRecord? record =
                configuration.GetConfiguration<PluginInstallSourceRecord>();

            if (record is null || !record.InstallSources.Remove(Key(pluginId)))
                return;

            configuration.SaveConfiguration(record);
        }
    }

    private static string Key(Ulid pluginId) => pluginId.ToString();
}
