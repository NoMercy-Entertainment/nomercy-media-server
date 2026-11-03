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

using Moq;
using NoMercy.Database.Models.Libraries;
using NoMercy.MediaProcessing.Music;
using NoMercy.Storage;

namespace NoMercy.Tests.MediaProcessing.Music;

/// <summary>
/// Issue #397: a music scan whose album path was spelled with the other separator
/// style, or with a trailing slash, did not match the library folder it lives in, so
/// the import ran with no folder and threw before anything was stored.
/// </summary>
[Trait("Category", "Unit")]
public sealed class LibraryFolderResolverTests
{
    [Fact]
    public void An_album_path_with_forward_slashes_resolves_to_a_backslashed_folder_root()
    {
        Folder folder = FolderAt("M:/Music");
        Library library = LibraryWithFolders(folder);
        IStorageFactory factory = FactoryResolving((folder, @"M:\Music"));

        LibraryFolderResolver
            .FolderHolding(library, "M:/Music/A/ABBA/[1976] Arrival", factory)
            .Should()
            .BeSameAs(folder, "the separator style does not change which folder holds the path");
    }

    [Fact]
    public void An_album_path_with_backslashes_resolves_to_a_forward_slashed_folder_root()
    {
        Folder folder = FolderAt("//nas/music");
        Library library = LibraryWithFolders(folder);
        IStorageFactory factory = FactoryResolving((folder, "//nas/music/"));

        LibraryFolderResolver
            .FolderHolding(library, @"\\nas\music\A\ABBA\[1976] Arrival", factory)
            .Should()
            .BeSameAs(folder, "the separator style does not change which folder holds the path");
    }

    [Fact]
    public void The_album_resolves_to_the_folder_that_holds_it_not_the_first()
    {
        Folder first = FolderAt("/srv/music");
        Folder second = FolderAt("/srv/music2");
        Library library = LibraryWithFolders(first, second);
        IStorageFactory factory = FactoryResolving((first, "/srv/music"), (second, "/srv/music2"));

        LibraryFolderResolver
            .FolderHolding(library, "/srv/music2/A/ABBA/[1976] Arrival", factory)
            .Should()
            .BeSameAs(second, "'/srv/music' is a sibling of '/srv/music2', not its parent");
    }

    [Fact]
    public void A_path_under_no_library_folder_resolves_to_nothing()
    {
        Folder folder = FolderAt("/srv/music");
        Library library = LibraryWithFolders(folder);
        IStorageFactory factory = FactoryResolving((folder, "/srv/music"));

        LibraryFolderResolver
            .FolderHolding(library, "/downloads/complete/ABBA - Arrival", factory)
            .Should()
            .BeNull();
    }

    internal static Library LibraryWithFolders(params Folder[] folders)
    {
        Library library = new() { Id = Ulid.NewUlid() };
        foreach (Folder folder in folders)
            library.FolderLibraries.Add(
                new(folder.Id, library.Id) { Folder = folder, Library = library }
            );
        return library;
    }

    internal static Folder FolderAt(string path) =>
        new()
        {
            Id = Ulid.NewUlid(),
            DriverId = Ulid.NewUlid(),
            Path = path,
        };

    /// <summary>
    /// A storage factory whose drivers resolve each folder to the given root, the way a
    /// real driver spells it (Local on Windows answers with backslashes).
    /// </summary>
    internal static IStorageFactory FactoryResolving(params (Folder Folder, string Root)[] roots)
    {
        Mock<IStorageFactory> factory = new();
        foreach ((Folder folder, string root) in roots)
        {
            Mock<IStorageDriver> driver = new();
            driver.Setup(d => d.GetFullPath(folder.Path)).Returns(root);
            Mock<IStorage> storage = new();
            storage.SetupGet(s => s.Driver).Returns(driver.Object);
            factory
                .Setup(f => f.For(folder.Id, folder.DriverId, string.Empty))
                .Returns(storage.Object);
        }

        return factory.Object;
    }
}
