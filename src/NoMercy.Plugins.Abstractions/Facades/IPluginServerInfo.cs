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
/// What this server is, so a plugin branches on a fact rather than on a guess.
/// <para>
/// Plugins used to read <c>Environment.OSVersion</c> and the entry assembly's
/// version to decide whether a feature was there, which answered for the process
/// and not for the contract. Both questions are answered here instead.
/// </para>
/// </summary>
public interface IPluginServerInfo
{
    /// <summary>The server's own version, not the contract's. The contract's is <see cref="PluginAbi" />.</summary>
    Version Version { get; }

    /// <summary><c>windows</c>, <c>linux</c> or <c>macos</c>.</summary>
    string Platform { get; }

    /// <summary>
    /// The folders the owner granted this plugin, in the same shape
    /// <see cref="IPluginStorageV3.PathAsync" /> takes an id for. Empty when the
    /// owner granted none, which is a plugin's cue to ask rather than to fail.
    /// </summary>
    IReadOnlyList<PluginStorageLocation> GrantedPaths { get; }

    /// <summary>
    /// Free space on a granted folder. Asynchronous because a granted folder can
    /// be NFS or S3, where answering means a round trip or an API call.
    /// <para>
    /// Not to be called before every write. Hold the answer for a few seconds;
    /// a plugin checking space per piece turns one write into one round trip.
    /// </para>
    /// </summary>
    Task<long> FreeSpaceBytesAsync(string folderId, CancellationToken ct = default);
}
