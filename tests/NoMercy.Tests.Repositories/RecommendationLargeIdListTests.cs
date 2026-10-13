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
using NoMercy.NmSystem.Extensions;
using NoMercy.Tests.Repositories.Infrastructure;

namespace NoMercy.Tests.Repositories;

/// <summary>
/// A large library has more unowned similar titles than SQLite allows variables in one
/// statement (32766). EF Core 10 sends every item of a <c>list.Contains(x)</c> as its own
/// parameter, so the recommendations endpoint crashed with "too many SQL variables".
/// These tests use the real <see cref="MediaContext"/> configuration, as the server does.
/// </summary>
public class RecommendationLargeIdListTests : IDisposable
{
    private const int UnownedSimilarCount = 33_000;

    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<MediaContext> _factory;

    public RecommendationLargeIdListTests()
    {
        string connectionString =
            $"DataSource={Guid.NewGuid()};Mode=Memory;Cache=Shared;Foreign Keys=True";
        _connection = new(connectionString);
        _connection.Open();
        _connection.CreateFunction(
            "normalize_search",
            (string? input) => input?.NormalizeSearch() ?? string.Empty
        );

        DbContextOptions<MediaContext> options = new DbContextOptionsBuilder<MediaContext>()
            .UseSqlite(connectionString)
            .Options;
        _factory = new MediaContextFactory(options);

        using MediaContext context = _factory.CreateDbContext();
        context.Database.EnsureCreated();
        TestMediaContextFactory.SeedData(context);

        // Movie 129 is in a library the seeded user can see; none of the targets is owned.
        int lastId = 1_000_000 + UnownedSimilarCount - 1;
        context.Database.ExecuteSql(
            $"""
            WITH RECURSIVE n(x) AS (SELECT 1000000 UNION ALL SELECT x + 1 FROM n WHERE x < {lastId})
            INSERT INTO Similar (MediaId, MovieFromId, Title) SELECT x, 129, 'Similar ' || x FROM n;
            """
        );
    }

    [Fact]
    public async Task GetUnownedMovieSimilarAsync_MoreIdsThanSqliteVariables_ReturnsEveryCandidate()
    {
        RecommendationRepository repository = new(_factory);

        List<RecommendationCandidateDto> candidates = await repository.GetUnownedMovieSimilarAsync(
            SeedConstants.UserId
        );

        candidates.Should().HaveCount(UnownedSimilarCount);
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed class MediaContextFactory(DbContextOptions<MediaContext> options)
        : IDbContextFactory<MediaContext>
    {
        public MediaContext CreateDbContext() => new(options);
    }
}
