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

using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NoMercy.Data.Repositories;
using NoMercy.Database;
using NoMercy.Database.Models.Common;

namespace NoMercy.Tests.Repositories;

[Trait("Category", "Unit")]
public sealed class ServerConfigurationRepositoryTests : IDisposable
{
    private static readonly Guid Admin = Guid.NewGuid();

    private readonly SqliteConnection _connection;
    private readonly AppDbContext _context;
    private readonly ServerConfigurationRepository _repository;

    public ServerConfigurationRepositoryTests()
    {
        _connection = new("Data Source=:memory:");
        _connection.Open();
        _context = new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();
        _repository = new(_context);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    private Configuration? Stored(string key) =>
        _context.Configuration.AsNoTracking().FirstOrDefault(row => row.Key == key);

    [Fact]
    public async Task GetValueAsync_UnknownKey_ReturnsNull()
    {
        (await _repository.GetValueAsync(ServerConfigurationKeys.ServerName)).Should().BeNull();
    }

    [Fact]
    public async Task SetValueAsync_InsertsThenUpdatesOneRow()
    {
        await _repository.SetValueAsync(ServerConfigurationKeys.ServerName, "first", Admin);
        await _repository.SetValueAsync(ServerConfigurationKeys.ServerName, "second", Admin);

        (await _repository.GetValueAsync(ServerConfigurationKeys.ServerName)).Should().Be("second");
        _context.Configuration.AsNoTracking().Count().Should().Be(1);
        Stored(ServerConfigurationKeys.ServerName)!.ModifiedBy.Should().Be(Admin);
    }

    [Fact]
    public async Task SetValueAsync_WithoutAUser_KeepsTheLastModifier()
    {
        await _repository.SetValueAsync("videoRunners", "2", Admin);
        await _repository.SetValueAsync("videoRunners", "4", null);

        Configuration row = Stored("videoRunners")!;
        row.Value.Should().Be("4");
        row.ModifiedBy.Should().Be(Admin);
    }
}
