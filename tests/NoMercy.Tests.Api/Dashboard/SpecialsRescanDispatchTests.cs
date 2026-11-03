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

using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using NoMercy.Api.Controllers.V1.Dashboard.Media;
using NoMercy.Data.Repositories;
using NoMercy.Data.Services;
using NoMercy.Database;
using NoMercy.Database.Models.TvShows;
using NoMercy.Storage;
using Xunit;

namespace NoMercy.Tests.Api.Dashboard;

/// <summary>
/// "Rescan all specials" loaded every special, built an empty title list and answered
/// "Rescanning all specials." without starting a single rescan. Rescanning one special
/// goes through <see cref="ILibraryLogicFactory"/>; rescanning all of them must take
/// that same path once per special.
/// </summary>
[Trait("Category", "Unit")]
public class SpecialsRescanDispatchTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly LibraryLogicFactory _libraryLogicFactory;

    public SpecialsRescanDispatchTests()
    {
        _connection = new("DataSource=:memory:");
        _connection.Open();
        DbContextOptions<MediaContext> options = new DbContextOptionsBuilder<MediaContext>()
            .UseSqlite(_connection)
            .Options;

        using MediaContext seedContext = new(options);
        seedContext.Database.EnsureCreated();

        _libraryLogicFactory = new(
            new TestDbContextFactory(options),
            Mock.Of<IStorageDriver>(),
            Mock.Of<IStorageFactory>(),
            Mock.Of<ILogger<LibraryLogic>>()
        );
    }

    public void Dispose() => _connection.Dispose();

    private static List<Special> TwoSpecials() =>
        [
            new() { Id = Ulid.NewUlid(), Title = "Directors cut" },
            new() { Id = Ulid.NewUlid(), Title = "Marathon" },
        ];

    private SpecialsController BuildController(
        List<Special> specials,
        Mock<ILibraryLogicFactory> libraryLogicFactory
    )
    {
        Mock<ISpecialRepository> specialRepository = new();
        specialRepository
            .Setup(repository => repository.GetAllSpecialsForRescanAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(specials);

        libraryLogicFactory
            .Setup(factory => factory.Create(It.IsAny<Ulid>()))
            .Returns((Ulid id) => _libraryLogicFactory.Create(id));

        return new(specialRepository.Object, libraryLogicFactory.Object);
    }

    [Fact]
    public async Task Rescanning_every_special_rescans_each_one()
    {
        List<Special> specials = TwoSpecials();
        Mock<ILibraryLogicFactory> libraryLogicFactory = new();

        IActionResult result = await BuildController(specials, libraryLogicFactory).RescanAll();

        result.Should().BeOfType<OkObjectResult>();
        foreach (Special special in specials)
        {
            libraryLogicFactory.Verify(
                factory => factory.Create(special.Id),
                Times.Once,
                $"rescan-all must rescan '{special.Title}' the way rescanning it alone does"
            );
        }
        libraryLogicFactory.Verify(
            factory => factory.Create(It.IsAny<Ulid>()),
            Times.Exactly(specials.Count)
        );
    }

    [Fact]
    public async Task Rescanning_every_special_with_none_present_is_not_found()
    {
        Mock<ILibraryLogicFactory> libraryLogicFactory = new();

        IActionResult result = await BuildController([], libraryLogicFactory).RescanAll();

        ObjectResult objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(404);
        libraryLogicFactory.Verify(factory => factory.Create(It.IsAny<Ulid>()), Times.Never);
    }

    private sealed class TestDbContextFactory(DbContextOptions<MediaContext> options)
        : IDbContextFactory<MediaContext>
    {
        public MediaContext CreateDbContext() => new(options);
    }
}
