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
using Microsoft.Extensions.Logging;
using Moq;
using NoMercy.Data.Services;
using NoMercy.Database;
using NoMercy.Storage;
using NoMercy.Tests.Repositories.Infrastructure;

namespace NoMercy.Tests.Repositories;

/// <summary>
/// <see cref="LibraryLogic"/> used to require its caller to own and dispose a
/// <see cref="MediaContext"/> passed in at construction — SpecialsController worked around
/// this with a scoped MediaContext injected for no other purpose. LibraryLogic now creates
/// its own context per <see cref="LibraryLogic.Process"/> call via the factory, like a job.
/// </summary>
[Trait("Category", "Characterization")]
public class LibraryLogicFactoryTests : IDisposable
{
    private readonly IDbContextFactory<MediaContext> _contextFactory;
    private readonly SqliteConnection _connection;
    private readonly LibraryLogicFactory _factory;

    public LibraryLogicFactoryTests()
    {
        (_contextFactory, _connection) = TestMediaContextFactory.CreateSeededFactory();
        _factory = new(
            _contextFactory,
            Mock.Of<IStorageDriver>(),
            Mock.Of<IStorageFactory>(),
            Mock.Of<ILogger<LibraryLogic>>()
        );
    }

    [Fact]
    public void Create_BuildsLibraryLogicWithTheGivenId()
    {
        Ulid libraryId = Ulid.NewUlid();

        LibraryLogic logic = _factory.Create(libraryId);

        logic.Id.Should().Be(libraryId);
    }

    [Fact]
    public async Task Process_UnknownLibrary_ReturnsFalse()
    {
        LibraryLogic logic = _factory.Create(Ulid.NewUlid());

        bool result = await logic.Process();

        result.Should().BeFalse();
    }

    [Fact]
    public async Task Process_KnownLibrary_ReturnsTrueWithoutAnExternallyOwnedContext()
    {
        // No MediaContext is created or passed in by the test — Process() resolves its
        // own from the factory, which is the behavior the refactor introduced.
        LibraryLogic logic = _factory.Create(SeedConstants.MovieLibraryId);

        bool result = await logic.Process();

        result.Should().BeTrue();
        logic.Titles.Should().BeEmpty();
    }

    public void Dispose()
    {
        _connection.Dispose();
    }
}
