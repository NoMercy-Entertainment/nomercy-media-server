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
using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugins.Storage;

/// <summary>
/// One folder the host owns on behalf of a plugin.
/// <para>
/// The guard is the whole class. An absolute path and a <c>..</c> segment are
/// the two ways out of a scope, and both refuse rather than resolve: a path
/// that resolves somewhere else is a plugin reading the server's own files
/// while every line of it looks like ordinary storage code.
/// </para>
/// </summary>
public class PluginLocalStorageScope(Ulid pluginId, string root) : IPluginStorageScope
{
    private readonly string _root = Path.GetFullPath(root);

    /// <summary>Null: this is not one of the owner's folders, it is one the server made.</summary>
    public PluginStorageLocation? Location => null;

    public Task<bool> ExistsAsync(string path, CancellationToken ct = default)
    {
        string full = Resolve(path);

        return Task.FromResult(File.Exists(full) || Directory.Exists(full));
    }

    /// <summary>
    /// The stream is the caller's to dispose, like every Open on this facade.
    /// Built rather than taken from <c>File.OpenRead</c> so the sharing mode is
    /// stated: the server reads its own library while a plugin is reading, and
    /// an exclusive handle would be the plugin locking the owner out of it.
    /// </summary>
    public Task<Stream> OpenReadAsync(string path, CancellationToken ct = default) =>
        Task.FromResult<Stream>(
            new FileStream(Resolve(path), FileMode.Open, FileAccess.Read, FileShare.Read)
        );

    public Task<Stream> OpenWriteAsync(string path, bool overwrite, CancellationToken ct = default)
    {
        string full = Resolve(path);

        if (Path.GetDirectoryName(full) is { } parent)
            Directory.CreateDirectory(parent);

        return Task.FromResult<Stream>(
            new FileStream(full, overwrite ? FileMode.Create : FileMode.CreateNew, FileAccess.Write)
        );
    }

    public Task DeleteAsync(string path, CancellationToken ct = default)
    {
        string full = Resolve(path);

        if (Directory.Exists(full))
            Directory.Delete(full, recursive: true);
        else if (File.Exists(full))
            File.Delete(full);

        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<PluginStorageEntry> ListAsync(
        string path,
        bool recursive = false,
        [EnumeratorCancellation] CancellationToken ct = default
    )
    {
        string full = Resolve(path);

        if (!Directory.Exists(full))
            yield break;

        SearchOption option = recursive
            ? SearchOption.AllDirectories
            : SearchOption.TopDirectoryOnly;

        foreach (string entry in Directory.EnumerateFileSystemEntries(full, "*", option))
        {
            ct.ThrowIfCancellationRequested();

            bool isDirectory = Directory.Exists(entry);

            // Relative to the scope's own root, so a path this answers with can
            // be handed straight back to it. An absolute one could not.
            yield return new(
                Path.GetRelativePath(_root, entry).Replace('\\', '/'),
                isDirectory,
                isDirectory ? 0 : new FileInfo(entry).Length,
                isDirectory ? Directory.GetLastWriteTimeUtc(entry) : File.GetLastWriteTimeUtc(entry)
            );
        }

        await Task.CompletedTask;
    }

    /// <summary>Empties the folder without removing it.</summary>
    public void Purge()
    {
        if (!Directory.Exists(_root))
            return;

        foreach (string entry in Directory.EnumerateFileSystemEntries(_root))
        {
            if (Directory.Exists(entry))
                Directory.Delete(entry, recursive: true);
            else
                File.Delete(entry);
        }
    }

    public void EnsureExists() => Directory.CreateDirectory(_root);

    private string Resolve(string path)
    {
        if (Path.IsPathRooted(path))
            Refuse(path);

        foreach (string segment in path.Split('/', '\\'))
        {
            if (segment == "..")
                Refuse(path);
        }

        string full = Path.GetFullPath(
            Path.Combine(_root, path.Replace('/', Path.DirectorySeparatorChar))
        );

        // Checked again after resolving, because a link inside the folder can
        // land somewhere the segment check could not see.
        if (!full.StartsWith(_root, StringComparison.OrdinalIgnoreCase))
            Refuse(path);

        return full;
    }

    private void Refuse(string path) =>
        throw new PluginRefusedException(
            PluginRefusalMessages.FileOutsideGrant(pluginId.ToString(), path)
        );
}
