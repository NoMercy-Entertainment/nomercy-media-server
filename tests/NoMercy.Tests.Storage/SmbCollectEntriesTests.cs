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

using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.Storage.Drivers.Smb;
using NoMercy.Tests.Storage.Fakes;
using SMBLibrary.Client;

namespace NoMercy.Tests.Storage;

/// <summary>
/// An SMB server applies the QUERY_DIRECTORY search pattern to every name in
/// a folder, subfolders included. Handing it the caller's file pattern hid
/// every folder the pattern did not name, so a recursive listing never
/// descended into them. The driver has to behave like the local driver: walk
/// every folder, return only the entries the pattern matches.
/// </summary>
[Trait("Category", "Unit")]
public sealed class SmbCollectEntriesTests
{
    private static SmbStorageDriver DriverOver(FakeSmbFileStore store) =>
        new(
            SmbDriverConfig.For("fake-server", "media"),
            NullLogger.Instance,
            () => new SmbSession { Client = new SMB2Client(), Store = store }
        );

    [Fact]
    public void AllDirectories_with_a_file_pattern_finds_files_below_folders_the_pattern_does_not_name()
    {
        FakeSmbFileStore store = new();
        store.SeedFile("A/B/movie.mkv");
        store.SeedFile("A/notes.txt");
        using SmbStorageDriver driver = DriverOver(store);

        IEnumerable<string> entries = driver.EnumerateFileSystemEntries(
            "/",
            "*.mkv",
            SearchOption.AllDirectories
        );

        entries.Should().Equal("/A/B/movie.mkv");
    }

    [Fact]
    public void TopDirectoryOnly_with_a_file_pattern_returns_only_the_matching_entries_of_that_folder()
    {
        FakeSmbFileStore store = new();
        store.SeedFile("movie.mkv");
        store.SeedFile("readme.txt");
        store.SeedDirectory("Season 1");
        using SmbStorageDriver driver = DriverOver(store);

        IEnumerable<string> entries = driver.EnumerateFileSystemEntries(
            "/",
            "*.mkv",
            SearchOption.TopDirectoryOnly
        );

        entries.Should().Equal("/movie.mkv");
    }

    [Fact]
    public void Wildcard_pattern_lists_folders_and_files_at_every_depth()
    {
        FakeSmbFileStore store = new();
        store.SeedFile("A/B/movie.mkv");
        using SmbStorageDriver driver = DriverOver(store);

        IEnumerable<string> entries = driver.EnumerateFileSystemEntries(
            "/",
            "*",
            SearchOption.AllDirectories
        );

        entries.Should().BeEquivalentTo("/A", "/A/B", "/A/B/movie.mkv");
    }
}
