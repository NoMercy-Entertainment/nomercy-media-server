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

        Change(record =>
        {
            if (source.Folder is not null)
                RemoveFolder(record, source.Folder);

            record.InstallSources[Key(pluginId)] = source;
            return true;
        });
    }

    public void Forget(Ulid pluginId) =>
        Change(record => record.InstallSources.Remove(Key(pluginId)));

    public void ForgetFolder(string folder) => Change(record => RemoveFolder(record, folder));

    public void Stage(string folder, Ulid pluginId, PluginInstallSource source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentNullException.ThrowIfNull(source);

        Change(record =>
        {
            string key = StagedKey(record, folder) ?? folder;
            record.StagedInstallSources[key] = new() { PluginId = pluginId, Source = source };
            return true;
        });
    }

    public void CommitStaged(string folder) =>
        Change(record =>
        {
            string? key = StagedKey(record, folder);
            if (key is null)
                return false;

            PluginStagedInstallSource staged = record.StagedInstallSources[key];
            record.StagedInstallSources.Remove(key);

            if (staged.Source.Folder is not null)
                RemoveFolder(record, staged.Source.Folder);

            record.InstallSources[Key(staged.PluginId)] = staged.Source;
            return true;
        });

    public void DropStaged(string folder) =>
        Change(record =>
            StagedKey(record, folder) is { } key && record.StagedInstallSources.Remove(key)
        );

    public IReadOnlyList<string> StagedFolders() =>
        configuration.GetConfiguration<PluginInstallSourceRecord>() is { } record
            ? [.. record.StagedInstallSources.Keys]
            : [];

    /// <summary>Reads, changes and saves under one gate; saves only when something changed.</summary>
    private void Change(Func<PluginInstallSourceRecord, bool> change)
    {
        lock (_gate)
        {
            PluginInstallSourceRecord record =
                configuration.GetConfiguration<PluginInstallSourceRecord>() ?? new();

            if (change(record))
                configuration.SaveConfiguration(record);
        }
    }

    private static bool RemoveFolder(PluginInstallSourceRecord record, string folder)
    {
        string[] stale =
        [
            .. record
                .InstallSources.Where(entry =>
                    string.Equals(entry.Value.Folder, folder, StringComparison.OrdinalIgnoreCase)
                )
                .Select(entry => entry.Key),
        ];

        foreach (string key in stale)
            record.InstallSources.Remove(key);

        return stale.Length > 0;
    }

    private static string? StagedKey(PluginInstallSourceRecord record, string folder) =>
        record.StagedInstallSources.Keys.FirstOrDefault(key =>
            string.Equals(key, folder, StringComparison.OrdinalIgnoreCase)
        );

    private static string Key(Ulid pluginId) => pluginId.ToString();
}
