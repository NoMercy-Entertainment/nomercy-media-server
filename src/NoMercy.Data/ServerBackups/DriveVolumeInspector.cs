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

using NoMercy.Storage;

namespace NoMercy.Data.ServerBackups;

/// <summary>
/// Finds a path's volume as the longest mount root that contains its real
/// location. Every link on the way (a symlink, or a junction on Windows) is
/// followed first, so a folder linked to another disk is seen on that disk.
/// Mount roots come from <see cref="DriveInfo.GetDrives"/>: drive letters on
/// Windows, the mount table on Linux and macOS. A volume mounted into a folder
/// on Windows is not listed there, so it is not told apart from its drive.
/// </summary>
public sealed class DriveVolumeInspector(
    IStorageDriver storage,
    Func<IReadOnlyList<string>>? mountRoots = null
) : IVolumeInspector
{
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private readonly Func<IReadOnlyList<string>> _mountRoots = mountRoots ?? ListMountRoots;

    public string? VolumeOf(string path)
    {
        string real = WithSeparator(RealPath(path));
        return _mountRoots()
            .Select(WithSeparator)
            .Where(root => real.StartsWith(root, PathComparison))
            .MaxBy(root => root.Length);
    }

    public bool IsMountPoint(string path)
    {
        string real = WithSeparator(RealPath(path));
        return _mountRoots().Select(WithSeparator).Any(root => root.Equals(real, PathComparison));
    }

    /// <summary>
    /// The path with every link along it replaced by its target, from the root
    /// down. Parts that do not exist yet are kept as they are.
    /// </summary>
    public string RealPath(string path)
    {
        string full = Path.GetFullPath(path);
        string root = Path.GetPathRoot(full) ?? string.Empty;
        string current = root;
        string[] parts = full[root.Length..]
            .Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries
            );

        foreach (string part in parts)
        {
            current = Path.Combine(current, part);
            string? target = storage.ResolveLinkTarget(current);
            if (target is not null)
                current = Path.GetFullPath(target);
        }

        return current;
    }

    private static List<string> ListMountRoots()
    {
        List<string> roots = [];
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            try
            {
                roots.Add(drive.RootDirectory.FullName);
            }
            catch (IOException)
            {
                // A drive that cannot be read (an empty card reader) holds no path of ours.
            }
            catch (UnauthorizedAccessException) { }
        }

        return roots;
    }

    private static string WithSeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;
}
