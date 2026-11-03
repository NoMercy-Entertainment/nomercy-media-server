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

namespace NoMercy.PluginSdk.Approval;

/// <summary>
/// What the owner approved of a plugin folder the server did not install
/// itself, keyed by the folder's name. Kept in the platform store beside
/// consent and grants, never in the plugin's own folder: anyone who can write
/// into that folder must not be able to write their own approval.
/// </summary>
public interface IPluginApprovalStore
{
    /// <summary>The approval on record for a folder, or null when the owner never gave one.</summary>
    PluginApproval? Find(string folder);

    /// <summary>Records the owner's yes to the folder as it is now, by its digest.</summary>
    void Approve(string folder, string digest);

    /// <summary>Drops the record, for a folder the server installed over or removed.</summary>
    void Forget(string folder);
}
