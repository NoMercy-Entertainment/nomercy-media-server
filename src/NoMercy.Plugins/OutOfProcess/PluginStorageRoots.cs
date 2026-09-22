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

namespace NoMercy.Plugins.OutOfProcess;

/// <summary>
/// Which folder a plugin means, answered by the server and opened by the
/// plugin.
/// <para>
/// Bytes do not cross the channel. A plugin writing a transcode through the
/// server would copy every one of them twice and hold the server's thread for
/// the length of the write, and the server would be doing file I/O on behalf
/// of code it does not trust.
/// </para>
/// <para>
/// So the server answers which folder, once, and the plugin's own process
/// opens the files. The sandbox is what makes that safe: the plugin's cgroup,
/// job object or profile is built from the same grants, so a path the owner
/// never granted is refused by the kernel and not only by our own check.
/// </para>
/// </summary>
public interface IPluginStorageRoots
{
    /// <summary>The plugin's own folders, which need no grant.</summary>
    string PrivateRoot { get; }

    string TempRoot { get; }

    string DerivedRoot { get; }

    /// <summary>
    /// The absolute path of one of the owner's folders, or null when this
    /// plugin holds no grant for it.
    /// </summary>
    Task<string?> PathForAsync(string folderId, CancellationToken ct = default);
}
