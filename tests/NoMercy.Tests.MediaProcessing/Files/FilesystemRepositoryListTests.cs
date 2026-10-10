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
using NoMercy.MediaProcessing.Files;
using NoMercy.NmSystem.Dto;
using NoMercy.Storage;

namespace NoMercy.Tests.MediaProcessing.Files;

/// <summary>
/// The dashboard folder picker draws whatever <see cref="FilesystemRepository.List"/>
/// returns, so an empty result is rendered as "this folder has no subfolders" and the
/// path stays pickable. A path the host cannot read must therefore never come back as
/// an empty result — a typo'd drive letter would otherwise be silently accepted as a
/// library root, and the picker offers no way out because the parent is empty too.
/// </summary>
[Trait("Category", "Unit")]
public class FilesystemRepositoryListTests
{
    private const string MissingFolder = "X:\\";
    private const string PresentFolder = "/media";

    [Fact]
    public void List_Throws_WhenFolderDoesNotExist()
    {
        Mock<IStorageDriver> driver = new(MockBehavior.Strict);
        driver.Setup(d => d.DirectoryExists(MissingFolder)).Returns(false);

        FilesystemRepository repository = new(driver.Object);

        Action list = () => repository.List(MissingFolder, withEmpty: false);

        list.Should().Throw<DirectoryNotFoundException>().WithMessage($"*{MissingFolder}*");
    }

    [Fact]
    public void List_PropagatesAccessDenied_RatherThanReportingAnEmptyFolder()
    {
        Mock<IStorageDriver> driver = new(MockBehavior.Strict);
        driver.Setup(d => d.DirectoryExists(PresentFolder)).Returns(true);
        driver
            .Setup(d =>
                d.EnumerateFileSystemEntries(PresentFolder, "*", SearchOption.TopDirectoryOnly)
            )
            .Throws(new UnauthorizedAccessException("denied"));

        FilesystemRepository repository = new(driver.Object);

        Action list = () => repository.List(PresentFolder, withEmpty: false);

        list.Should().Throw<UnauthorizedAccessException>();
    }

    [Fact]
    public void List_PropagatesIoFailure_RatherThanReportingAnEmptyFolder()
    {
        Mock<IStorageDriver> driver = new(MockBehavior.Strict);
        driver.Setup(d => d.DirectoryExists(PresentFolder)).Returns(true);
        driver
            .Setup(d =>
                d.EnumerateFileSystemEntries(PresentFolder, "*", SearchOption.TopDirectoryOnly)
            )
            .Throws(new IOException("device is not ready"));

        FilesystemRepository repository = new(driver.Object);

        Action list = () => repository.List(PresentFolder, withEmpty: false);

        list.Should().Throw<IOException>();
    }

    [Fact]
    public void List_ReturnsEmpty_WhenNoFolderIsChosen()
    {
        Mock<IStorageDriver> driver = new(MockBehavior.Strict);

        FilesystemRepository repository = new(driver.Object);

        (string? parent, List<DirectoryTree> entries) = repository.List("", withEmpty: false);

        parent.Should().BeNull();
        entries.Should().BeEmpty();
    }

    [Fact]
    public void List_ReturnsEmpty_WhenFolderExistsButHasNoSubfolders()
    {
        Mock<IStorageDriver> driver = new(MockBehavior.Strict);
        driver.Setup(d => d.DirectoryExists(PresentFolder)).Returns(true);
        driver
            .Setup(d =>
                d.EnumerateFileSystemEntries(PresentFolder, "*", SearchOption.TopDirectoryOnly)
            )
            .Returns([]);

        FilesystemRepository repository = new(driver.Object);

        (string? parent, List<DirectoryTree> entries) = repository.List(
            PresentFolder,
            withEmpty: false
        );

        parent.Should().NotBeNull();
        entries.Should().BeEmpty();
    }
}
