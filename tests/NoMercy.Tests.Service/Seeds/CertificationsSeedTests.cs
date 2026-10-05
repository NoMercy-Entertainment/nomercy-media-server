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
using NoMercy.Service.Seeds;

namespace NoMercy.Tests.Service.Seeds;

/// <summary>
/// <see cref="CertificationsSeed.Init"/> must never call out to TMDB when
/// certifications already exist — every boot otherwise pays a real network
/// round trip (and, for a rate-limited/offline install, a Warning log) for
/// data that's already there. This pins the early-return guard using a real
/// in-memory <see cref="MediaContext"/> rather than a mock of the type under
/// test.
/// </summary>
[Trait("Category", "Unit")]
public sealed class CertificationsSeedTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MediaContext> _options;

    public CertificationsSeedTests()
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
    public async Task Init_CertificationsAlreadySeeded_ReturnsWithoutCallingNetwork()
    {
        await using MediaContext seedContext = new(_options);
        seedContext.Certifications.Add(
            new()
            {
                Iso31661 = "US",
                Rating = "PG-13",
                Meaning = "Parents strongly cautioned",
                Order = 3,
            }
        );
        await seedContext.SaveChangesAsync();

        await using MediaContext context = new(_options);

        // No network access is configured in this test process; if Init()
        // attempted the TMDB fetch here it would throw or hang instead of
        // returning promptly. Called via the declaring type (not extension
        // syntax) — NoMercy.Service.Seeds declares several other MediaContext
        // "Init" extensions that would otherwise make `context.Init()` ambiguous.
        await CertificationsSeed.Init(context);

        int count = await context.Certifications.CountAsync();
        Assert.Equal(1, count);
    }
}
