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
using NoMercy.NmSystem.Information;
using NoMercy.Service.Seeds;
using NoMercy.Storage;

namespace NoMercy.Tests.Service.Seeds;

/// <summary>
/// <see cref="LibrariesSeed.Init"/> must no-op cleanly when the libraries seed
/// file (<see cref="AppFiles.LibrariesSeedFile"/>) is absent — the common case
/// on every boot after the first. It must never reach the folder-roots stage,
/// the DB, or the dynamic-static-files folder registration in that case.
/// </summary>
[Trait("Category", "Unit")]
public sealed class LibrariesSeedTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MediaContext> _options;

    public LibrariesSeedTests()
    {
        _connection = new("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<MediaContext>()
            .UseSqlite(
                _connection,
                o => o.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery)
            )
            .Options;

        using MediaContext ctx = new(_options);
        ctx.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task Init_LibrariesSeedFileMissing_ReturnsBeforeCheckingFolderRoots()
    {
        Mock<IStorage> storage = new(MockBehavior.Strict);
        storage.Setup(s => s.Exists(AppFiles.LibrariesSeedFile)).Returns(false);
        Mock<IStorageDriver> driver = new();

        await using MediaContext context = new(_options);

        await LibrariesSeed.Init(context, storage.Object, driver.Object);

        int libraryCount = await context.Libraries.CountAsync();
        Assert.Equal(0, libraryCount);
        // Strict mock: a call to check FolderRootsSeedFile (or anything else)
        // would have thrown — proves the method returned at the FIRST guard.
        storage.Verify(s => s.Exists(AppFiles.LibrariesSeedFile), Times.Once);
    }
}
