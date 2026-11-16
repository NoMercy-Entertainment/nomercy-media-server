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

namespace NoMercy.Data.ServerBackups;

/// <summary>
/// Finds a path's volume as the longest mount root that contains it, from
/// <see cref="DriveInfo.GetDrives"/>: drive letters on Windows, the mount table
/// on Linux and macOS. A volume mounted into a folder on Windows is not listed
/// there, so it is not told apart from its drive.
/// </summary>
public sealed class DriveVolumeInspector : IVolumeInspector
{
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    public string? VolumeOf(string path)
    {
        string full = WithSeparator(Path.GetFullPath(path));
        return MountRoots()
            .Where(root => full.StartsWith(root, PathComparison))
            .MaxBy(root => root.Length);
    }

    public bool IsMountPoint(string path)
    {
        string full = WithSeparator(Path.GetFullPath(path));
        return MountRoots().Any(root => root.Equals(full, PathComparison));
    }

    private static List<string> MountRoots()
    {
        List<string> roots = [];
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            try
            {
                roots.Add(WithSeparator(drive.RootDirectory.FullName));
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
