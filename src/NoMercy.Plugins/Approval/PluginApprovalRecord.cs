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
/// The shape the approvals take in the platform config file. Named
/// distinctly from the consent and grant records' properties: all three share
/// one file, and a save replaces a top-level key wholesale for whichever
/// record wrote it last.
/// </summary>
public class PluginApprovalRecord
{
    /// <summary>Keyed by plugin folder name, compared without case like the folder itself.</summary>
    public Dictionary<string, PluginApproval> DropApprovals { get; init; } = [];
}
