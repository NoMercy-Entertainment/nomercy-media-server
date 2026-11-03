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

namespace NoMercy.PluginSdk.Approval;

/// <summary>
/// Approvals in the platform-scoped config, the same file consent and grants
/// live in. Folder names are matched without case, as the recorded-folder set
/// matches them, so a folder is one entry whatever the filesystem's casing.
/// </summary>
public class ConfigPluginApprovalStore(IPluginConfiguration configuration) : IPluginApprovalStore
{
    public PluginApproval? Find(string folder)
    {
        PluginApprovalRecord? record = configuration.GetConfiguration<PluginApprovalRecord>();
        if (record is null)
            return null;

        string? key = KeyOf(record, folder);

        return key is null ? null : record.DropApprovals[key];
    }

    public void Approve(string folder, string digest)
    {
        PluginApprovalRecord record =
            configuration.GetConfiguration<PluginApprovalRecord>() ?? new();

        string key = KeyOf(record, folder) ?? folder;
        record.DropApprovals[key] = new() { Digest = digest };

        configuration.SaveConfiguration(record);
    }

    public void Forget(string folder)
    {
        PluginApprovalRecord? record = configuration.GetConfiguration<PluginApprovalRecord>();
        if (record is null)
            return;

        string? key = KeyOf(record, folder);
        if (key is null)
            return;

        record.DropApprovals.Remove(key);
        configuration.SaveConfiguration(record);
    }

    private static string? KeyOf(PluginApprovalRecord record, string folder) =>
        record.DropApprovals.Keys.FirstOrDefault(key =>
            string.Equals(key, folder, StringComparison.OrdinalIgnoreCase)
        );
}
