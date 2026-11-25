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

using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NoMercy.Data.ServerBackups;
using NoMercy.Database;
using NoMercy.Storage;

namespace NoMercy.Api.Services;

public sealed class StorageReportService(
    IStorageDriver storage,
    StorageLocations locations,
    IDbContextFactory<MediaContext> mediaContextFactory,
    IDbContextFactory<QueueContext> queueContextFactory,
    IVolumeInspector volumes,
    ILogger<StorageReportService> logger
) : IStorageReportService
{
    private const string OriginalFolder = "original";
    private const string MusicFolder = "music";
    private const string TempFolder = "temp";

    public async Task<StorageReport> ReadAsync(CancellationToken cancellationToken)
    {
        List<StoragePart> parts =
        [
            await DatabasePartAsync(
                "database_media",
                locations.MediaDatabase,
                mediaContextFactory,
                cancellationToken
            ),
            await DatabasePartAsync(
                "database_queue",
                locations.QueueDatabase,
                queueContextFactory,
                cancellationToken
            ),
            new("database_app", "databases", DatabaseBytes(locations.AppDatabase), 0),
            .. BackupParts(),
            .. ImageParts(),
            Folder("logs", "logs", locations.LogPath),
            Folder("transcode", "temp", locations.TranscodePath),
            Folder("temp", "temp", locations.TempPath),
        ];

        return new(parts, volumes.FreeBytesAt(locations.DataRoot));
    }

    private async Task<StoragePart> DatabasePartAsync<TContext>(
        string id,
        string path,
        IDbContextFactory<TContext> factory,
        CancellationToken cancellationToken
    )
        where TContext : DbContext
    {
        long free = 0;
        try
        {
            await using TContext context = await factory.CreateDbContextAsync(cancellationToken);
            await context.Database.OpenConnectionAsync(cancellationToken);
            DbConnection connection = context.Database.GetDbConnection();
            long pageSize = await ScalarAsync(connection, "PRAGMA page_size;", cancellationToken);
            long freePages = await ScalarAsync(
                connection,
                "PRAGMA freelist_count;",
                cancellationToken
            );
            free = pageSize * freePages;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "Free pages of {Part} could not be read: {Reason}",
                id,
                exception.Message
            );
        }

        return new(id, "databases", DatabaseBytes(path), free);
    }

    private static async Task<long> ScalarAsync(
        DbConnection connection,
        string sql,
        CancellationToken cancellationToken
    )
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    private long DatabaseBytes(string path) => FileBytes(path) + FileBytes(path + "-wal");

    private long FileBytes(string path) => storage.FileExists(path) ? storage.GetFileSize(path) : 0;

    private IEnumerable<StoragePart> BackupParts()
    {
        long sets = FolderBytes(Path.Combine(locations.BackupRoot, "sets"));
        long copies =
            TopLevelFileBytes(locations.BackupRoot, _ => true)
            + TopLevelFileBytes(
                locations.DataRoot,
                name => DatabaseCopyName.TryParseHandMade(name, out string _)
            );

        yield return new("backup_sets", "backups", sets, 0);
        yield return new("backup_copies", "backups", copies, 0);
    }

    private IEnumerable<StoragePart> ImageParts()
    {
        Dictionary<string, long> bytes = new()
        {
            [OriginalFolder] = 0,
            [MusicFolder] = 0,
            [TempFolder] = 0,
            ["other"] = 0,
        };
        foreach (StorageEntryInfo file in Files(locations.ImagesPath))
        {
            string first = Path.GetRelativePath(locations.ImagesPath, file.Path)
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
            string bucket = bytes.ContainsKey(first) && first != "other" ? first : "other";
            bytes[bucket] += file.Size;
        }

        yield return new("image_original", "image_cache", bytes[OriginalFolder], 0);
        yield return new("image_music", "image_cache", bytes[MusicFolder], 0);
        yield return new("image_temp", "image_cache", bytes[TempFolder], 0);
        yield return new("image_other", "image_cache", bytes["other"], 0);
    }

    private StoragePart Folder(string id, string group, string path) =>
        new(id, group, FolderBytes(path), 0);

    private long FolderBytes(string path) => Files(path).Sum(file => file.Size);

    private long TopLevelFileBytes(string folder, Func<string, bool> include) =>
        !storage.DirectoryExists(folder)
            ? 0
            : storage
                .EnumerateEntries(folder, "*", SearchOption.TopDirectoryOnly)
                .Where(entry => !entry.IsDirectory && include(Path.GetFileName(entry.Path)))
                .Sum(entry => entry.Size);

    private IEnumerable<StorageEntryInfo> Files(string folder) =>
        !storage.DirectoryExists(folder)
            ? []
            : storage
                .EnumerateEntries(folder, "*", SearchOption.AllDirectories)
                .Where(entry => !entry.IsDirectory);
}
