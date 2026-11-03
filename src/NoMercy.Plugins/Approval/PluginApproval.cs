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
/// One owner approval: the digest of every file in the plugin folder at the
/// moment the owner said yes. A folder that no longer hashes to it is not
/// the one that was approved.
/// </summary>
public class PluginApproval
{
    public string Digest { get; init; } = string.Empty;
}
