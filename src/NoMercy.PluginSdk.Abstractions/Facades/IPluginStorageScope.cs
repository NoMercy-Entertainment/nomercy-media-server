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

namespace NoMercy.PluginSdk.Abstractions;

/// <summary>
/// One place, opened. Every path is relative to it and uses forward slashes,
/// exactly as the server's own storage already requires, so the guards that are
/// already there keep applying and a plugin cannot walk out of its scope.
/// </summary>
public interface IPluginStorageScope
{
    /// <summary>
    /// The server folder this scope was opened on. Null for the scopes the host
    /// owns rather than the owner: the plugin's private folder, temp and derived.
    /// </summary>
    PluginStorageLocation? Location { get; }

    Task<bool> ExistsAsync(string path, CancellationToken ct = default);

    Task<Stream> OpenReadAsync(string path, CancellationToken ct = default);

    Task<Stream> OpenWriteAsync(string path, bool overwrite, CancellationToken ct = default);

    Task DeleteAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// What is in a folder. An empty path lists the scope root. Every entry's
    /// path is relative to the scope, so it can be handed straight back to the
    /// other members here.
    /// </summary>
    IAsyncEnumerable<PluginStorageEntry> ListAsync(
        string path,
        bool recursive = false,
        CancellationToken ct = default
    );

    /// <summary>
    /// Marks a file in this scope sparse, so the disk holds only the parts
    /// written to it. On NTFS a file that is not sparse has its whole length
    /// reserved the moment the length is set, and every write past the data
    /// written so far zero-fills the gap first: a download written in the
    /// order its pieces arrive then takes about twice as long.
    /// <para>
    /// Call it on a file that exists, right after creating it and before
    /// setting its length. The writer may still hold it open.
    /// </para>
    /// <para>
    /// Best effort, and never a reason to stop: true when the flag was set,
    /// false when nothing was done. That is every platform but Windows,
    /// where files are sparse already, a folder that is not a local disk,
    /// and a file system that refuses, such as FAT32 on a USB disk. Added in
    /// ABI 12.3.
    /// </para>
    /// </summary>
    Task<bool> MarkSparseAsync(string path, CancellationToken ct = default) =>
        Task.FromResult(false);
}

/// <param name="Path">Relative to the scope, forward slashes, safe to pass back to this scope.</param>
/// <param name="SizeBytes">Zero for a directory.</param>
public sealed record PluginStorageEntry(
    string Path,
    bool IsDirectory,
    long SizeBytes,
    DateTimeOffset LastModified
);
