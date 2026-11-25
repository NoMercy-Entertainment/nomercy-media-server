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

using NoMercy.NmSystem.Information;

namespace NoMercy.Api.Services;

/// <summary>The folders and files the storage report measures.</summary>
public sealed record StorageLocations(
    string MediaDatabase,
    string QueueDatabase,
    string AppDatabase,
    string DataRoot,
    string BackupRoot,
    string ImagesPath,
    string LogPath,
    string TranscodePath,
    string TempPath
)
{
    public static StorageLocations ForAppFiles() =>
        new(
            AppFiles.MediaDatabase,
            AppFiles.QueueDatabase,
            AppFiles.AppDatabase,
            AppFiles.DataPath,
            Path.Combine(AppFiles.DataPath, "backups"),
            AppFiles.ImagesPath,
            AppFiles.LogPath,
            AppFiles.TranscodePath,
            AppFiles.TempPath
        );
}
