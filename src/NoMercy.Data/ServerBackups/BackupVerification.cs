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

namespace NoMercy.Data.ServerBackups;

/// <summary>
/// The verdict on a backup set. <see cref="Manifest"/> is set whenever the
/// manifest could be read, even when other problems were found.
/// </summary>
public sealed record BackupVerification(BackupManifest? Manifest, IReadOnlyList<string> Problems)
{
    public bool IsValid => Manifest is not null && Problems.Count == 0;
}
