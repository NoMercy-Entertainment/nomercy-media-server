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

using NoMercy.PluginSdk.Abstractions;
using NoMercy.Storage;

namespace NoMercy.PluginSdk.Media;

/// <summary>
/// One plugin's own folder, as the server serves files out of it.
/// <para>
/// The server reads these files on the plugin's behalf and hands them to a
/// player, so the folder's edge is the whole guard. An absolute path, a
/// <c>..</c> segment, and a path that resolves outside are refused. So is a
/// link anywhere between the folder and the file: a plugin in its own process
/// can make one, and a link that led out would have the server read a file
/// the plugin itself is not allowed to.
/// </para>
/// </summary>
internal sealed class PluginDataFolder(Ulid pluginId, string root, IStorage storage)
{
    private readonly string _root = Path.GetFullPath(root);

    /// <summary>The full path of a file inside the folder, or a refusal.</summary>
    public string Resolve(string relativePath)
    {
        if (
            string.IsNullOrWhiteSpace(relativePath)
            || Path.IsPathRooted(relativePath)
            || relativePath.Split('/', '\\').Contains("..")
        )
            throw Refused(relativePath);

        string full = Path.GetFullPath(
            Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar))
        );

        if (
            !full.StartsWith(
                _root + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase
            )
        )
            throw Refused(relativePath);

        // Every step from the folder down to the file, so a link anywhere on
        // the way is caught, not only one at the end.
        string current = _root;
        foreach (
            string segment in Path.GetRelativePath(_root, full).Split(Path.DirectorySeparatorChar)
        )
        {
            current = Path.Combine(current, segment);

            if (storage.Driver.ResolveLinkTarget(current) is not null)
                throw Refused(relativePath);
        }

        return full;
    }

    /// <summary>The file, if it is still there and still inside; null when it is gone.</summary>
    public Stream? OpenRead(string relativePath)
    {
        string full = Resolve(relativePath);

        return storage.Driver.FileExists(full) ? storage.OpenRead(full) : null;
    }

    public bool Holds(string relativePath) => storage.Driver.FileExists(Resolve(relativePath));

    private PluginRefusedException Refused(string path) =>
        new(PluginRefusalMessages.FileOutsideGrant(pluginId.ToString(), path));
}
