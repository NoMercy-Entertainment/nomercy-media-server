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

using System.Collections.Concurrent;
using NoMercy.NmSystem.Dto;
using NoMercy.Storage.Drivers.Local;

namespace NoMercy.Tests.NmSystem;

/// <summary>
/// A <c>.back</c> folder is the original kept during a re-encode, so the library
/// scan must not list it as a title of its own.
/// </summary>
[Trait("Category", "Unit")]
public sealed class MediaScanSkipsBackFoldersTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "nm_scan_back_" + Guid.NewGuid().ToString("N")
    );

    public MediaScanSkipsBackFoldersTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Made.Up.Title.(2013)"));
        Directory.CreateDirectory(Path.Combine(_root, "Made.Up.Title.(2013).back"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task LibraryRootWithATitleAndItsBackCopy_ListsOnlyTheTitle()
    {
        await using MediaScan mediaScan = new(new LocalStorageDriver());

        ConcurrentBag<MediaFolderExtend> folders = await mediaScan.Process(_root, 1);

        folders.Select(folder => folder.Name).Should().BeEquivalentTo("Made.Up.Title.(2013)");
    }
}
