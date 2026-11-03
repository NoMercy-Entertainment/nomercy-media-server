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

using NoMercy.Database.Models.Libraries;
using NoMercy.Storage;

namespace NoMercy.MediaProcessing.Music;

/// <summary>
/// Which of a library's folders holds a scanned path.
/// </summary>
public static class LibraryFolderResolver
{
    /// <summary>
    /// The library folder whose resolved root holds <paramref name="path"/>, or null
    /// when none does.
    /// <para>
    /// Each folder's root is resolved through its own driver, not the IStorage facade:
    /// the facade's GetFullPath is a LocalStorage-only escape hatch that throws on every
    /// remote backend.
    /// </para>
    /// <para>
    /// A driver spells its root its own way: Local on Windows answers "M:\Music" for the
    /// stored "M:/Music", a share root may end in a slash. A raw prefix test then missed
    /// the album the scan had just found inside that very root, and matched a sibling
    /// such as "M:\Music2" as if it were a child. Both sides are compared as whole
    /// forward-slash segments instead.
    /// </para>
    /// </summary>
    public static Folder? FolderHolding(
        Library library,
        string path,
        IStorageFactory storageFactory
    ) =>
        library
            .FolderLibraries.Select(folderLibrary => folderLibrary.Folder)
            .FirstOrDefault(folder => IsWithinRoot(path, RootOf(folder, storageFactory)));

    /// <summary>The folder's root as its own driver spells it.</summary>
    public static string RootOf(Folder folder, IStorageFactory storageFactory)
    {
        IStorage folderStorage = storageFactory.For(folder.Id, folder.DriverId, string.Empty);
        return folderStorage.Driver.GetFullPath(folder.Path);
    }

    private static bool IsWithinRoot(string path, string root)
    {
        string normalizedPath = path.Replace('\\', '/').TrimEnd('/');
        string normalizedRoot = root.Replace('\\', '/').TrimEnd('/');

        if (normalizedRoot.Length == 0)
            return normalizedPath.Length > 0;

        if (!normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            return false;

        return normalizedPath.Length == normalizedRoot.Length
            || normalizedPath[normalizedRoot.Length] == '/';
    }
}
