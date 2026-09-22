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

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NoMercy.Database;
using NoMercy.PluginSdk;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Capabilities;
using NoMercy.Storage;

namespace NoMercy.Data.Plugins;

/// <summary>
/// The jail a plugin writes inside.
/// <para>
/// Two checks stand between a plugin and a user's media, and both have to pass
/// on every call: the plugin must hold a
/// <see cref="PluginGrantKind.LibraryWrite"/> grant for the library the path
/// belongs to, and the path must actually resolve inside that library's root.
/// The second is what stops a granted plugin walking out through
/// <c>../../</c> — a grant names a library, not a starting point.
/// </para>
/// <para>
/// Every operation goes through <see cref="IStorage"/> rather than
/// <c>System.IO</c>, so the path guard the rest of the server relies on applies
/// here too and the work is auditable in one place. A plugin holding this can
/// reach media directories and nothing else.
/// </para>
/// </summary>
public class PluginLibraryWriter(
    Ulid pluginId,
    IDbContextFactory<MediaContext> contextFactory,
    IStorageFactory storageFactory,
    IPluginGrantStore grants,
    ILogger logger
) : IPluginLibraryWriter
{
    public async Task<IReadOnlyList<PluginLibrary>> GetWritableLibrariesAsync(
        CancellationToken ct = default
    )
    {
        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);

        List<PluginLibrary> libraries = await context
            .Libraries.AsNoTracking()
            .Select(library => new PluginLibrary(
                library.Id.ToString(),
                library.Title,
                library.Type
            ))
            .ToListAsync(ct);

        return
        [
            .. libraries.Where(library =>
                grants.Holds(pluginId, PluginGrantKind.LibraryWrite, library.Id)
            ),
        ];
    }

    public async Task<bool> CanWriteAsync(string path, CancellationToken ct = default)
    {
        (IStorage? storage, string? _) = await ResolveAsync(path, ct);
        return storage is not null;
    }

    public async Task RecycleAsync(string path, CancellationToken ct = default)
    {
        (IStorage storage, string libraryId) = await RequireAsync(path, ct);

        // Recycle rather than delete is what makes an upgrade reversible: the
        // superseded file leaves the library so nothing sees two copies of one
        // episode, and the owner can still get it back if the replacement turns
        // out to be worse.
        logger.LogInformation(
            "Plugin {PluginId} recycling {Path} in library {LibraryId}",
            pluginId,
            path,
            libraryId
        );

        string recycled = RecyclePathFor(path);
        await storage.CreateDirectoryAsync(DirectoryOf(recycled), ct);
        await storage.MoveAsync(path, recycled, ct);
    }

    public async Task DeleteAsync(string path, CancellationToken ct = default)
    {
        (IStorage storage, string libraryId) = await RequireAsync(path, ct);

        logger.LogWarning(
            "Plugin {PluginId} permanently deleting {Path} in library {LibraryId}",
            pluginId,
            path,
            libraryId
        );

        await storage.DeleteAsync(path, ct);
    }

    public async Task MoveAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken ct = default
    )
    {
        // Both ends. Checking only the source would let a granted plugin move a
        // file out of the library entirely, which is a delete wearing a
        // different name.
        (IStorage storage, string libraryId) = await RequireAsync(sourcePath, ct);
        await RequireAsync(destinationPath, ct);

        logger.LogInformation(
            "Plugin {PluginId} moving {Source} to {Destination} in library {LibraryId}",
            pluginId,
            sourcePath,
            destinationPath,
            libraryId
        );

        await storage.MoveAsync(sourcePath, destinationPath, ct);
    }

    private async Task<(IStorage Storage, string LibraryId)> RequireAsync(
        string path,
        CancellationToken ct
    )
    {
        (IStorage? storage, string? libraryId) = await ResolveAsync(path, ct);

        if (storage is null || libraryId is null)
            throw new PluginLibraryAccessDeniedException(
                path,
                "the path is not inside a library this plugin has been granted write access to"
            );

        return (storage, libraryId);
    }

    /// <summary>
    /// Where a recycled file goes: a <c>.recycled</c> directory at the library
    /// root, which is inside the jail. There is no recycle bin on the storage
    /// facade, so this is a move the plugin was already allowed to make rather
    /// than a new capability.
    /// </summary>
    internal static string RecyclePathFor(string path)
    {
        string normalised = path.Replace('\\', '/');
        int cut = normalised.LastIndexOf('/');
        string name = cut < 0 ? normalised : normalised[(cut + 1)..];
        string directory = cut <= 0 ? string.Empty : normalised[..cut];

        return $"{directory}/.recycled/{name}";
    }

    private static string DirectoryOf(string path)
    {
        int cut = path.LastIndexOfAny(['/', '\\']);
        return cut <= 0 ? string.Empty : path[..cut];
    }

    /// <summary>
    /// Whether <paramref name="path"/> is inside <paramref name="root"/>,
    /// compared the way the filesystem reads it: separators differ between a
    /// folder record and whatever a plugin hands in, and a prefix match without
    /// the trailing separator would let "/media/Anime2" pass as inside
    /// "/media/Anime".
    /// </summary>
    internal static bool IsUnderRoot(string? root, string path)
    {
        if (string.IsNullOrWhiteSpace(root))
            return false;

        string normalisedRoot = root.Replace('\\', '/').TrimEnd('/');
        string normalisedPath = path.Replace('\\', '/');

        if (normalisedPath.Contains("/../") || normalisedPath.EndsWith("/.."))
            return false;

        return normalisedPath.StartsWith(normalisedRoot + "/", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<(IStorage? Storage, string? LibraryId)> ResolveAsync(
        string path,
        CancellationToken ct
    )
    {
        if (string.IsNullOrWhiteSpace(path))
            return (null, null);

        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);

        var folders = await context
            .FolderLibrary.AsNoTracking()
            .Select(folderLibrary => new
            {
                LibraryId = folderLibrary.LibraryId,
                FolderId = folderLibrary.Folder.Id,
                DriverId = folderLibrary.Folder.DriverId,
                FolderPath = folderLibrary.Folder.Path,
            })
            .ToListAsync(ct);

        foreach (var folder in folders)
        {
            string libraryId = folder.LibraryId.ToString();

            if (!grants.Holds(pluginId, PluginGrantKind.LibraryWrite, libraryId))
                continue;

            IStorage storage = storageFactory.For(
                folder.FolderId,
                folder.DriverId,
                folder.FolderPath ?? string.Empty
            );

            if (IsUnderRoot(folder.FolderPath, path))
                return (storage, libraryId);
        }

        return (null, null);
    }
}

/// <summary>Builds a writer bound to one plugin's grants.</summary>
public class PluginLibraryWriterFactory(
    IDbContextFactory<MediaContext> contextFactory,
    IStorageFactory storageFactory,
    IPluginGrantStore grants,
    ILoggerFactory loggerFactory
) : IPluginLibraryWriterFactory
{
    public IPluginLibraryWriter? CreateFor(Ulid pluginId)
    {
        // The capability says the plugin wants this; the grant says the owner
        // allowed it. No grant, no writer — and the context exposes null, so a
        // plugin can see the difference without calling and catching.
        if (grants.Granted(pluginId, PluginGrantKind.LibraryWrite).Count == 0)
            return null;

        return new PluginLibraryWriter(
            pluginId,
            contextFactory,
            storageFactory,
            grants,
            loggerFactory.CreateLogger<PluginLibraryWriter>()
        );
    }
}
