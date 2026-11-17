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
using NoMercy.Data.ServerBackups;
using NoMercy.Service.Seeds;

namespace NoMercy.Tests.Service.ServerBackups;

/// <summary>The real EF Core contexts behind a backup's database names.</summary>
[Trait("Category", "Integration")]
public sealed class BackupDatabaseSchemaTests : IDisposable
{
    private readonly BackupTestFolder _folder = new();
    private readonly BackupDatabaseSchema _schema = new();

    public void Dispose() => _folder.Dispose();

    [Theory]
    [InlineData("app")]
    [InlineData("media")]
    [InlineData("queue")]
    public void KnownMigrations_ListsTheShippedMigrationsInOrder(string name)
    {
        IReadOnlyList<string> migrations = _schema.KnownMigrations(name);

        migrations.Should().NotBeEmpty();
        migrations.Should().BeInAscendingOrder(StringComparer.Ordinal);
    }

    [Fact]
    public void KnownMigrations_OfAnUnknownDatabase_Throws()
    {
        Action act = () => _schema.KnownMigrations("other");

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData("app")]
    [InlineData("queue")]
    public async Task Migrate_BringsAnOlderCopyFullyUpToDate(string name)
    {
        string path = _folder.DatabasePath(name);
        IReadOnlyList<string> known = _schema.KnownMigrations(name);

        await _schema.MigrateAsync(name, path, CancellationToken.None);

        SqliteConnection.ClearAllPools();
        new SqliteFileOperations().ReadAppliedMigrations(path).Should().Equal(known);
    }
}
