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

namespace NoMercy.PluginSdk.Provenance;

/// <summary>
/// Where each installed plugin came from, written by the install itself. Kept
/// in the platform store beside consent, grants and approvals, never in the
/// plugin's own folder: a plugin must not be able to say it came from
/// somewhere it did not.
/// </summary>
public interface IPluginInstallSourceStore
{
    /// <summary>The recorded source, or null for a plugin installed before sources were recorded.</summary>
    PluginInstallSource? Find(Ulid pluginId);

    /// <summary>
    /// Records an install or an update, replacing what an earlier install
    /// recorded for this id and for the folder it names: one folder holds one
    /// plugin, so a record for another id in that folder is about files that
    /// are gone.
    /// </summary>
    void Record(Ulid pluginId, PluginInstallSource source);

    /// <summary>Drops the record, for a plugin that was uninstalled.</summary>
    void Forget(Ulid pluginId);

    /// <summary>Drops any record about the plugin in this folder, whatever its id.</summary>
    void ForgetFolder(string folder);

    /// <summary>Holds the source of an update staged for the next start, until it is applied.</summary>
    void Stage(string folder, Ulid pluginId, PluginInstallSource source);

    /// <summary>The staged copy in this folder was applied: its source becomes the plugin's.</summary>
    void CommitStaged(string folder);

    /// <summary>The staged copy in this folder was replaced or discarded: its source is dropped.</summary>
    void DropStaged(string folder);

    /// <summary>The folders with a staged source waiting.</summary>
    IReadOnlyList<string> StagedFolders();
}
