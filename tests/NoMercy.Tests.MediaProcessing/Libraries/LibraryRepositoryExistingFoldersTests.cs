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

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Music;
using NoMercy.Database.Models.Storage;
using NoMercy.MediaProcessing.Libraries;
using NoMercy.NmSystem.Extensions;
using NoMercy.Storage.Drivers.Local;

namespace NoMercy.Tests.MediaProcessing.Libraries;

public class LibraryRepositoryExistingFoldersTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly MediaContext _context;

    public LibraryRepositoryExistingFoldersTests()
    {
        string dbName = Guid.NewGuid().ToString();
        _connection = new($"DataSource={dbName};Mode=Memory;Cache=Shared");
        _connection.Open();
        _connection.CreateFunction(
            "normalize_search",
            (string? input) => input?.NormalizeSearch() ?? string.Empty
        );

        DbContextOptions<MediaContext> options = new DbContextOptionsBuilder<MediaContext>()
            .UseSqlite(
                _connection,
                o => o.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery)
            )
            .AddInterceptors(new SqliteNormalizeSearchInterceptor())
            .Options;

        _context = new(options);
        _context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    private async Task<(Library Library, Folder Folder)> SeedLibraryAsync(string title)
    {
        Driver driver = new()
        {
            Id = Ulid.NewUlid(),
            Name = $"test-driver-{Ulid.NewUlid()}",
            Type = "local",
        };
        _context.Drivers.Add(driver);

        Library library = new()
        {
            Id = Ulid.NewUlid(),
            Title = title,
            Type = "music",
        };
        Folder folder = new()
        {
            Id = Ulid.NewUlid(),
            Path = "/music",
            DriverId = driver.Id,
        };
        _context.Libraries.Add(library);
        _context.Folders.Add(folder);
        await _context.SaveChangesAsync();
        return (library, folder);
    }

    private async Task SeedAlbumAsync(Library library, Folder folder, string path)
    {
        Album album = new()
        {
            Id = Guid.NewGuid(),
            Name = "Test Album",
            Folder = path,
            HostFolder = path,
            LibraryId = library.Id,
            Library = library,
            FolderId = folder.Id,
            LibraryFolder = folder,
        };
        _context.Albums.Add(album);
        await _context.SaveChangesAsync();
        _context.AlbumLibrary.Add(new(album.Id, library.Id));
        await _context.SaveChangesAsync();
    }

    [Fact]
    public async Task GetExistingFolderNamesAsync_MusicLibrary_ReturnsImportedAlbumFolders()
    {
        (Library library, Folder folder) = await SeedLibraryAsync("Test Music");
        await SeedAlbumAsync(library, folder, "/Test Artist/Test Album");

        LibraryRepository repo = new(_context, new LocalStorageDriver());

        HashSet<string> existing = await repo.GetExistingFolderNamesAsync(library.Id, "music");

        Assert.Contains("Test Album".NormalizeForComparison(), existing);
    }

    [Fact]
    public async Task GetExistingFolderNamesAsync_MusicLibrary_IgnoresAlbumsOfOtherLibraries()
    {
        (Library library, _) = await SeedLibraryAsync("Test Music");
        (Library other, Folder otherFolder) = await SeedLibraryAsync("Other Music");
        await SeedAlbumAsync(other, otherFolder, "/Test Album");

        LibraryRepository repo = new(_context, new LocalStorageDriver());

        HashSet<string> existing = await repo.GetExistingFolderNamesAsync(library.Id, "music");

        Assert.Empty(existing);
    }
}
