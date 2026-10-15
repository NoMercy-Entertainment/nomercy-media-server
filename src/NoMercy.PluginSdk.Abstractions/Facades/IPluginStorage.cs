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
/// Every place a plugin may read and write, as the owner granted them.
/// <para>
/// An earlier SDK offered the plugin's own folder as a string and the server's
/// libraries not at all, so a plugin that produced files either wrote beside its
/// own database or took an absolute path the owner typed. Both were outside
/// anything the owner could later see or revoke.
/// </para>
/// <para>
/// Every scope here is relative-path only. An absolute path or a <c>..</c>
/// segment refuses with <see cref="PluginRefusalCodes.FileOutsideGrant" />
/// rather than resolving.
/// </para>
/// </summary>
public interface IPluginStorage
{
    /// <summary>The plugin's own folder. Always present; counted against the disk quota.</summary>
    IPluginStorageScope Private { get; }

    /// <summary>
    /// A folder the owner picked on the permissions page, by its host-issued id.
    /// Refuses with <see cref="PluginRefusalCodes.FileOutsideGrant" /> when the
    /// id names a folder this plugin was not granted.
    /// </summary>
    Task<IPluginStorageScope> PathAsync(string folderId, CancellationToken ct = default);

    /// <summary>Scratch space the host purges. Nothing here survives a restart.</summary>
    IPluginStorageScope Temp { get; }

    /// <summary>The derived audio and video store, keyed by content and evicted least-recently-used.</summary>
    IPluginStorageScope Derived { get; }

    /// <summary>
    /// The caller's own corner. Anything a plugin holds about a person belongs
    /// here, so it can be exported and erased as one thing.
    /// </summary>
    IPluginUserScope ForUser { get; }

    /// <summary>
    /// A SQLite database inside the private folder, opened and migrated by the
    /// host. The torrent plugin hand-rolled 2300 lines of ADO to get here.
    /// <para>
    /// Asynchronous because opening is where the host applies the plugin's
    /// migrations, which is real work on a table a plugin has been filling for
    /// months.
    /// </para>
    /// </summary>
    Task<IPluginDatabase> OpenDatabaseAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// Marks a file at a full local path sparse, for a file that is not in a
    /// scope: a torrent client's incomplete downloads, for instance, which it
    /// writes a piece at a time through handles it keeps open, in a folder
    /// the owner picked in the plugin's own settings.
    /// <para>
    /// The same call as <see cref="IPluginStorageScope.MarkSparseAsync" />:
    /// on a file that exists, right after creating it and before setting its
    /// length, while the writer may still hold it open. It marks nothing the
    /// plugin could not already write itself.
    /// </para>
    /// <para>
    /// Best effort, and never a reason to stop: true when the flag was set,
    /// false when nothing was done. That is every platform but Windows, a
    /// path that is not a full local path to an existing file (a relative
    /// or a network path included), and a file system that refuses. Added
    /// in ABI 12.4.
    /// </para>
    /// </summary>
    Task<bool> MarkSparseAsync(string localPath, CancellationToken ct = default) =>
        Task.FromResult(false);
}
