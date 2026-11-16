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

namespace NoMercy.Data.ServerBackups;

/// <summary>
/// Where backups live and what goes into one. <see cref="ForAppFiles"/> gives the
/// server's real folders; tests build their own with temporary folders.
/// </summary>
public sealed class BackupOptions
{
    public const int DefaultRetainCount = 5;

    /// <summary>
    /// The backups folder. Single-database copies made before a migration sit at
    /// its top level; complete sets live under <see cref="SetsRoot"/>.
    /// </summary>
    public required string BackupRoot { get; init; }

    /// <summary>The settings folder that a set copies whole.</summary>
    public required string ConfigRoot { get; init; }

    /// <summary>The databases that a set copies.</summary>
    public required IReadOnlyList<BackupDatabaseFile> Databases { get; init; }

    /// <summary>How many copies to keep: per database before a migration, and complete sets.</summary>
    public int RetainCount { get; init; } = DefaultRetainCount;

    /// <summary>The server version written into each manifest.</summary>
    public string ServerVersion { get; init; } = "0.0.0";

    public string SetsRoot => Path.Combine(BackupRoot, "sets");

    /// <summary>
    /// Holds the pending-restore request, the result of the last restore and the
    /// work folder a restore stages in. It sits under the data folder, next to the
    /// live databases, so moving a staged file into place is a rename on one volume.
    /// </summary>
    public string RestoreRoot => Path.Combine(BackupRoot, "restore");

    /// <summary>The restore request that the next start applies.</summary>
    public string PendingRestorePath => Path.Combine(RestoreRoot, "pending.json");

    /// <summary>How the last restore ended.</summary>
    public string LastRestorePath => Path.Combine(RestoreRoot, "last-result.json");

    /// <summary>Staged copies, the pre-restore files and the swap journal while a restore runs.</summary>
    public string RestoreWorkRoot => Path.Combine(RestoreRoot, "work");

    public static BackupOptions ForAppFiles()
    {
        Version version = Software.Version ?? new Version(0, 0, 0);
        return new()
        {
            BackupRoot = Path.Combine(AppFiles.DataPath, "backups"),
            ConfigRoot = AppFiles.ConfigPath,
            Databases =
            [
                new("app", AppFiles.AppDatabase),
                new("media", AppFiles.MediaDatabase),
                new("queue", AppFiles.QueueDatabase),
            ],
            ServerVersion = $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}",
        };
    }
}
