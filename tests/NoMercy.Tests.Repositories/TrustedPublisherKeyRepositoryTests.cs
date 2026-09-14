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
using NoMercy.Database.Models.Media;
using NoMercy.Tests.Repositories.Infrastructure;

namespace NoMercy.Tests.Repositories;

[Trait("Category", "Unit")]
public sealed class TrustedPublisherKeyRepositoryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly MediaContext _context;
    private readonly TrustedPublisherKeyRepository _repository;

    public TrustedPublisherKeyRepositoryTests()
    {
        (IDbContextFactory<MediaContext> factory, _connection) =
            TestMediaContextFactory.CreateSeededFactory();
        _context = factory.CreateDbContext();
        _repository = new(_context);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    private static TrustedPublisherKey Key(string fingerprint, DateTime addedAt) =>
        new()
        {
            Fingerprint = fingerprint,
            Label = fingerprint,
            PublicKeyBase64 = "AAAA",
            AddedAt = addedAt,
            AddedBy = "test",
        };

    [Fact]
    public async Task AddThenList_ReturnsKeysOldestFirst()
    {
        await _repository.AddAsync(Key("newer", DateTime.UtcNow));
        await _repository.AddAsync(Key("older", DateTime.UtcNow.AddDays(-1)));

        List<TrustedPublisherKey> keys = await _repository.GetAllAsync();

        keys.Select(k => k.Fingerprint).Should().Equal("older", "newer");
        (await _repository.ExistsAsync("older")).Should().BeTrue();
        (await _repository.ExistsAsync("missing")).Should().BeFalse();
    }

    [Fact]
    public async Task DeleteAsync_ReportsWhetherAKeyWasRemoved()
    {
        await _repository.AddAsync(Key("gone", DateTime.UtcNow));

        (await _repository.DeleteAsync("gone")).Should().BeTrue();
        (await _repository.DeleteAsync("gone")).Should().BeFalse();
        (await _repository.ExistsAsync("gone")).Should().BeFalse();
    }
}
