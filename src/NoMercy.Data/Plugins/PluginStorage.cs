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

using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.Storage;

namespace NoMercy.Data.Plugins;

/// <summary>
/// The server side of <see cref="IPluginFolderCatalog" />.
///
/// <para>
/// A location is one of the server's own folders, with the driver that reaches
/// it. That is what makes a library on NFS or S3 writable by a plugin without
/// the plugin knowing what either is: the same facade the server uses for its
/// own media does the reaching, and the same guards apply to the paths.
/// </para>
/// </summary>
public class PluginStorage(
    IDbContextFactory<MediaContext> contextFactory,
    IStorageFactory storageFactory
) : IPluginFolderCatalog
{
    public async Task<IReadOnlyList<PluginStorageLocation>> LocationsAsync(
        CancellationToken ct = default
    )
    {
        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);

        List<Folder> folders = await context
            .Folders.AsNoTracking()
            .Include(folder => folder.Driver)
            .ToListAsync(ct);

        return
        [
            .. folders
                .Where(folder => folder.Driver is not null)
                .Select(folder => new PluginStorageLocation(
                    folder.Id.ToString(),
                    // What the owner would recognise: the driver they named, and
                    // the path underneath it. A bare Ulid is not a place anyone
                    // can pick from a list.
                    $"{folder.Driver!.Name} - {folder.Path}",
                    folder.Driver.Type,
                    true
                )),
        ];
    }

    public async Task<IPluginStorageScope?> OpenAsync(
        string locationId,
        CancellationToken ct = default
    )
    {
        if (!Ulid.TryParse(locationId, out Ulid folderId))
            return null;

        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);

        Folder? folder = await context
            .Folders.AsNoTracking()
            .Include(item => item.Driver)
            .FirstOrDefaultAsync(item => item.Id == folderId, ct);

        if (folder?.Driver is null)
            return null;

        // Opened at the folder root. Every path a plugin then gives is relative
        // to it, which is what keeps a plugin inside the place it was handed.
        IStorage storage = storageFactory.For(folder.Id, folder.DriverId, string.Empty);

        PluginStorageLocation location = new(
            folder.Id.ToString(),
            $"{folder.Driver.Name} - {folder.Path}",
            folder.Driver.Type,
            true
        );

        return new PluginStorageScope(location, storage);
    }
}

/// <summary>One folder, opened, with the server's own guards still in front of it.</summary>
internal class PluginStorageScope(PluginStorageLocation location, IStorage storage)
    : IPluginStorageScope
{
    public PluginStorageLocation? Location { get; } = location;

    public Task<bool> ExistsAsync(string path, CancellationToken ct = default)
    {
        return storage.ExistsAsync(path, ct);
    }

    public Task<Stream> OpenReadAsync(string path, CancellationToken ct = default)
    {
        return storage.OpenReadAsync(path, ct);
    }

    public Task<Stream> OpenWriteAsync(string path, bool overwrite, CancellationToken ct = default)
    {
        return storage.OpenWriteAsync(path, overwrite, ct);
    }

    public Task DeleteAsync(string path, CancellationToken ct = default)
    {
        return storage.DeleteAsync(path, ct);
    }

    public async IAsyncEnumerable<PluginStorageEntry> ListAsync(
        string path,
        bool recursive = false,
        [EnumeratorCancellation] CancellationToken ct = default
    )
    {
        await foreach (StorageEntry entry in storage.ListAsync(path, null, recursive, ct))
        {
            yield return new PluginStorageEntry(
                entry.Path,
                entry.IsDirectory,
                entry.SizeBytes,
                entry.LastModified
            );
        }
    }
}
