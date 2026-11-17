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

/// <summary>A published backup set, as the list of backups shows it.</summary>
public sealed record BackupSetSummary(
    string Id,
    DateTime CreatedAt,
    string Reason,
    string ServerVersion,
    long SizeBytes,
    IReadOnlyList<BackupManifestDatabase> Databases,
    int ConfigFileCount
)
{
    public static BackupSetSummary From(BackupManifest manifest) =>
        new(
            manifest.Id,
            manifest.CreatedAt,
            manifest.Reason,
            manifest.ServerVersion,
            manifest.Databases.Sum(database => database.Size)
                + manifest.ConfigFiles.Sum(file => file.Size),
            manifest.Databases,
            manifest.ConfigFiles.Count
        );
}
