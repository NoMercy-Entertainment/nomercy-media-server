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
using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.Database;
using NoMercy.Database.Models.People;
using NoMercy.MediaProcessing.People;
using Type = NoMercy.MediaProcessing.People.Type;

namespace NoMercy.Tests.MediaProcessing.People;

/// <summary>
/// PersonManager stores every crew job under a credit id normalised to the empty
/// string, but hands the crew row itself the raw nullable id. A crew credit that
/// arrived without a credit_id therefore never found its job, and the lookup
/// threw before the upsert ran — so one such credit cost the title every crew
/// row it had.
/// </summary>
public sealed class PersonRepositoryStoreCrewTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MediaContext> _options;

    public PersonRepositoryStoreCrewTests()
    {
        _connection = new("Data Source=:memory:");
        _connection.Open();

        using (SqliteCommand fkOff = _connection.CreateCommand())
        {
            fkOff.CommandText = "PRAGMA foreign_keys = OFF;";
            fkOff.ExecuteNonQuery();
        }

        _options = new DbContextOptionsBuilder<MediaContext>().UseSqlite(_connection).Options;

        using MediaContext ctx = new(_options);
        ctx.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    [Fact]
    public async Task StoreCrew_a_credit_without_a_credit_id_does_not_cost_the_title_its_other_crew()
    {
        using MediaContext context = new(_options);
        PersonRepository repository = new(context, NullLogger<PersonRepository>.Instance);

        // The people come first, as PersonManager stores them; reading crew back
        // joins them for the adult-content filter.
        List<Person> people =
        [
            new() { Id = 1, Name = "Director Person" },
            new() { Id = 2, Name = "Writer Person" },
        ];
        await repository.Store(people);

        // What PersonManager hands over for a crew list holding one id-less
        // credit: its job keyed on the empty string, its crew row on the raw null.
        List<Job> jobs =
        [
            new() { CreditId = string.Empty, Task = "Director" },
            new() { CreditId = "credit-writer", Task = "Writer" },
        ];
        await repository.StoreJobs(jobs);

        List<Crew> crew =
        [
            new()
            {
                CreditId = null,
                PersonId = 1,
                MovieId = 10,
            },
            new()
            {
                CreditId = "credit-writer",
                PersonId = 2,
                MovieId = 10,
            },
        ];

        Func<Task> store = () => repository.StoreCrew(crew, Type.Movie);

        await store.Should().NotThrowAsync();

        using MediaContext verify = new(_options);
        Job writer = await verify
            .Jobs.AsNoTracking()
            .SingleAsync(job => job.CreditId == "credit-writer");
        List<Crew> stored = await verify.Crews.AsNoTracking().ToListAsync();

        stored.Should().ContainSingle();
        stored[0].CreditId.Should().Be("credit-writer");
        stored[0].PersonId.Should().Be(2);
        stored[0].JobId.Should().Be(writer.Id);
    }
}
