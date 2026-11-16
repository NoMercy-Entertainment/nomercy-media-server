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
/// One SQLite database that belongs in a backup set.
/// </summary>
/// <param name="Name">Short name the manifest uses: <c>app</c>, <c>media</c> or <c>queue</c>.</param>
/// <param name="LivePath">Absolute path of the database the server runs on.</param>
public sealed record BackupDatabaseFile(string Name, string LivePath)
{
    public string FileName => Path.GetFileName(LivePath);
}
