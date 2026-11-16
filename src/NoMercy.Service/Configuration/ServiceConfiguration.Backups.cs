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

using NoMercy.Data.ServerBackups;

namespace NoMercy.Service.Configuration;

public static partial class ServiceConfiguration
{
    private static void ConfigureBackups(IServiceCollection services)
    {
        services.AddSingleton(_ => BackupOptions.ForAppFiles());
        services.AddSingleton<ISqliteFileOperations, SqliteFileOperations>();
        services.AddSingleton<IPreMigrationBackup, PreMigrationBackup>();
        services.AddSingleton<IBackupService, BackupService>();
    }
}
