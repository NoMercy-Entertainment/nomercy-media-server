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
/// The places the server can write, as the owner sees them.
///
/// <para>
/// A plugin that produces files had nowhere sanctioned to put them.
/// <see cref="IPluginContext.DataFolderPath" /> is the plugin's own corner,
/// which is right for a database and wrong for a four-gigabyte episode.
/// Anything else was an absolute path the owner typed, unvalidated, on whatever
/// machine the server happens to be. And a library on NFS or S3 was unreachable
/// to a plugin altogether, though the server itself reaches it happily.
/// </para>
///
/// <para>
/// Paths are relative to the scope and use forward slashes, exactly as the
/// server's own storage already requires, so the guards that are already there
/// keep applying and a plugin cannot walk out of its scope.
/// </para>
///
/// <para>
/// Not every folder a plugin uses can come through here, and that is on purpose.
/// A torrent client writes its incomplete downloads with random access - a piece
/// at a time, at byte offsets, through handles it keeps open for reading and
/// writing at once because it seeds out of the same handle it downloaded into. A
/// whole-file facade cannot serve that and it must stay a real local path. The
/// staging destination is the opposite: one file, written once, read once by the
/// encoder. That is the kind of place this is for.
/// </para>
/// </summary>
public interface IPluginStorage
{
    /// <summary>Every place the server can write, as the owner sees them.</summary>
    Task<IReadOnlyList<PluginStorageLocation>> LocationsAsync(CancellationToken ct = default);

    /// <summary>One of them, to read and write through. Null when the id is not one the server knows.</summary>
    Task<IPluginStorageScope?> OpenAsync(string locationId, CancellationToken ct = default);
}

/// <param name="Kind">local, nfs, smb, s3 or webdav - what the owner would call it.</param>
/// <param name="Writable">Whether the server can write here, which the owner should not have to guess.</param>
public sealed record PluginStorageLocation(string Id, string Name, string Kind, bool Writable);
