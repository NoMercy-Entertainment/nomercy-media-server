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
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Users;
using NoMercy.Tests.Repositories.Infrastructure;

namespace NoMercy.Tests.Repositories;

[Trait("Category", "Characterization")]
public class UserRepositoryTests : IDisposable
{
    private static readonly Guid ActingUserId = Guid.Parse("99999999-9999-9999-9999-999999999999");

    private readonly MediaContext _context;
    private readonly IDbContextFactory<MediaContext> _factory;
    private readonly SqliteConnection _connection;
    private readonly UserRepository _repository;

    public UserRepositoryTests()
    {
        (_factory, _connection) = TestMediaContextFactory.CreateSeededFactory();
        _context = _factory.CreateDbContext();

        // A separate administrator performing the update. It must exist so that a
        // mis-targeted LibraryUser write would satisfy the FK and surface as a
        // wrong-owner row rather than a constraint failure.
        _context.Users.Add(
            new()
            {
                Id = ActingUserId,
                Email = "admin@nomercy.tv",
                Name = "Acting Admin",
                Allowed = true,
                Manage = true,
            }
        );
        _context.SaveChanges();

        _repository = new(_context, _factory);
    }

    [Fact]
    public async Task UpdatePermissionsAsync_GrantsLibraryAccessToTargetUser_NotActingUser()
    {
        await _repository.UpdatePermissionsAsync(
            targetUserId: SeedConstants.UserId,
            actingUserId: ActingUserId,
            allowed: true,
            opticalAccess: false,
            audioTranscoding: false,
            videoTranscoding: false,
            noTranscoding: false,
            manage: true,
            libraryIds: [SeedConstants.MovieLibraryId]
        );

        await using MediaContext verify = _factory.CreateDbContext();
        List<LibraryUser> rows = await verify
            .LibraryUser.Where(lu => lu.LibraryId == SeedConstants.MovieLibraryId)
            .ToListAsync();

        Assert.Contains(rows, lu => lu.UserId == SeedConstants.UserId);
        Assert.DoesNotContain(rows, lu => lu.UserId == ActingUserId);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task CountAllowedAsync_CountsAllowedUsersAndOwnersButNotBlockedUsers()
    {
        _context.Users.Add(
            new()
            {
                Id = Guid.NewGuid(),
                Email = "blocked@nomercy.tv",
                Name = "Blocked",
                Allowed = false,
                Owner = false,
            }
        );
        await _context.SaveChangesAsync();

        int count = await _repository.CountAllowedAsync();

        count.Should().Be(2);
    }
}
