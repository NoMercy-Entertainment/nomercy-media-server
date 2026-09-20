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

namespace NoMercy.Plugins.Abstractions;

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
}

/// <param name="Path">Relative to the scope, forward slashes, safe to pass back to this scope.</param>
/// <param name="SizeBytes">Zero for a directory.</param>
public sealed record PluginStorageEntry(
    string Path,
    bool IsDirectory,
    long SizeBytes,
    DateTimeOffset LastModified
);
