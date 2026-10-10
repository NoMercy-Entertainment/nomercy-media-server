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
using NoMercy.Database;
using NoMercy.MediaProcessing.Files;
using NoMercy.NmSystem.Dto;
using NoMercy.Storage;

namespace NoMercy.Tests.MediaProcessing.Files;

[Trait("Category", "Unit")]
public class FileRepositoryDirectoryTreeTests
{
    private const string Folder = "/media";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GetDirectoryTree_ReturnsEmpty_WhenEnumerationFails(bool accessDenied)
    {
        Mock<IStorageDriver> driver = new(MockBehavior.Strict);
        driver.Setup(d => d.DirectoryExists(Folder)).Returns(true);
        driver.Setup(d => d.DirectoryExists("/media/first")).Returns(true);
        driver
            .Setup(d => d.EnumerateFileSystemEntries(Folder, "*", SearchOption.TopDirectoryOnly))
            .Returns(FailingEntries(accessDenied));

        using MediaContext context = new();
        FileRepository repository = new(context, driver.Object);

        List<DirectoryTree> entries = repository.GetDirectoryTree(Folder);

        entries.Should().BeEmpty();
    }

    private static IEnumerable<string> FailingEntries(bool accessDenied)
    {
        yield return "/media/first";

        if (accessDenied)
            throw new UnauthorizedAccessException("denied during enumeration");

        throw new IOException("device failed during enumeration");
    }
}
