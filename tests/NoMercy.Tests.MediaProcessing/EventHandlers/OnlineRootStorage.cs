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
using NoMercy.Storage;

namespace NoMercy.Tests.MediaProcessing.EventHandlers;

/// <summary>
/// A storage factory whose library roots answer like a mounted share: the root exists
/// and holds entries. The file delete guard needs that before it believes a delete.
/// </summary>
internal static class OnlineRootStorage
{
    public static IStorageFactory Factory(IStorageDriver driver)
    {
        Mock<IStorage> storage = new();
        storage.SetupGet(s => s.Driver).Returns(driver);
        storage.Setup(s => s.GetFullPath(It.IsAny<string>())).Returns((string path) => path);

        Mock<IStorageFactory> factory = new();
        factory
            .Setup(f => f.For(It.IsAny<Ulid>(), It.IsAny<Ulid>(), It.IsAny<string>()))
            .Returns(storage.Object);
        return factory.Object;
    }

    // Only the library roots are directories, so a renamed file is not read as a renamed folder.
    public static IStorageDriver Driver(
        string[] roots,
        bool rootReadable = true,
        bool rootHasEntries = true
    )
    {
        Mock<IStorageDriver> driver = new();
        driver
            .Setup(d => d.DirectoryExists(It.IsAny<string>()))
            .Returns((string path) => rootReadable && roots.Contains(path));
        driver
            .Setup(d =>
                d.EnumerateFileSystemEntries(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<SearchOption>()
                )
            )
            .Returns(rootHasEntries ? ["/entry"] : []);
        return driver.Object;
    }
}
