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
using NoMercy.Plugins.Abstractions;
using NoMercy.Plugins.Capabilities;
using NoMercy.Plugins.Storage;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// The database a plugin gets instead of hand-rolling one: migrations that run
/// once, parameters that are bound, and a read that does not hold every row.
/// </summary>
public class PluginDatabaseTests : IDisposable
{
    private static readonly Ulid Plugin = Ulid.Parse("01J9ZK5V8Y0000000000000027");

    private static readonly string[] Schema =
    [
        "CREATE TABLE pieces (id INTEGER PRIMARY KEY, title TEXT)",
    ];

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"nm-plugin-db-{Ulid.NewUlid()}"
    );

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);

        GC.SuppressFinalize(this);
    }

    private Task<PluginDatabase> OpenAsync(string name = "state") =>
        PluginDatabase.OpenAsync(Path.Combine(_root, "data", Plugin.ToString(), $"{name}.sqlite"));

    private PluginHostStorage Storage() =>
        new(Plugin, _root, new StubCatalog(), new PluginHostStorageTests.StubGrants(Plugin));

    [Fact]
    public async Task A_migration_creates_the_table_it_names()
    {
        await using PluginDatabase database = await OpenAsync();

        await database.MigrateAsync(Schema);

        await database.ExecuteAsync("INSERT INTO pieces (id, title) VALUES (1, 'one')");

        (await database.ScalarAsync("SELECT COUNT(*) FROM pieces")).Should().Be(1L);
    }

    [Fact]
    public async Task The_same_migration_list_runs_once_however_often_it_is_handed_over()
    {
        await using PluginDatabase database = await OpenAsync();

        await database.MigrateAsync(Schema);
        await database.MigrateAsync(Schema);

        (await database.ScalarAsync($"SELECT MAX(applied) FROM {PluginDatabase.VersionTable}"))
            .Should()
            .Be(1L, "a second run of CREATE TABLE would have thrown");
    }

    [Fact]
    public async Task A_migration_added_later_runs_and_the_earlier_ones_do_not()
    {
        await using PluginDatabase database = await OpenAsync();
        await database.MigrateAsync(Schema);
        await database.ExecuteAsync("INSERT INTO pieces (id, title) VALUES (1, 'kept')");

        await database.MigrateAsync([.. Schema, "ALTER TABLE pieces ADD COLUMN done INTEGER"]);

        (await database.ScalarAsync("SELECT title FROM pieces WHERE id = 1"))
            .Should()
            .Be("kept", "re-running the first statement would have dropped the row with the table");
        (await database.ScalarAsync("SELECT COUNT(done) FROM pieces")).Should().Be(0L);
    }

    [Fact]
    public async Task A_migration_that_fails_leaves_the_ones_before_it_applied()
    {
        await using PluginDatabase database = await OpenAsync();

        await Assert.ThrowsAsync<SqliteException>(() =>
            database.MigrateAsync([.. Schema, "CREATE TABLE ( this is not sql"])
        );

        (await database.ScalarAsync($"SELECT MAX(applied) FROM {PluginDatabase.VersionTable}"))
            .Should()
            .Be(1L, "one transaction each means a half-built schema is never the state");
    }

    [Fact]
    public async Task What_was_written_survives_the_database_being_closed_and_opened_again()
    {
        await using (PluginDatabase first = await OpenAsync())
        {
            await first.MigrateAsync(Schema);
            await first.ExecuteAsync("INSERT INTO pieces (id, title) VALUES (7, 'stays')");
        }

        await using PluginDatabase second = await OpenAsync();

        await second.MigrateAsync(Schema);

        (await second.ScalarAsync("SELECT title FROM pieces WHERE id = 7")).Should().Be("stays");
    }

    [Fact]
    public async Task A_value_with_a_quote_in_it_is_stored_as_written()
    {
        await using PluginDatabase database = await OpenAsync();
        await database.MigrateAsync(Schema);

        await database.ExecuteAsync(
            "INSERT INTO pieces (id, title) VALUES ($id, $title)",
            new Dictionary<string, object?> { ["$id"] = 1, ["$title"] = "Rock 'n' Roll" }
        );

        (await database.ScalarAsync("SELECT title FROM pieces WHERE id = 1"))
            .Should()
            .Be("Rock 'n' Roll", "a bound parameter is never read as SQL");
    }

    [Fact]
    public async Task A_parameter_carrying_its_own_sql_is_a_value_and_not_a_statement()
    {
        await using PluginDatabase database = await OpenAsync();
        await database.MigrateAsync(Schema);

        await database.ExecuteAsync(
            "INSERT INTO pieces (id, title) VALUES ($id, $title)",
            new Dictionary<string, object?>
            {
                ["$id"] = 1,
                ["$title"] = "x'); DROP TABLE pieces; --",
            }
        );

        (await database.ScalarAsync("SELECT COUNT(*) FROM pieces"))
            .Should()
            .Be(1L, "the table is still there and holds the text as a title");
    }

    [Fact]
    public async Task A_null_parameter_is_stored_as_null_and_read_back_as_null()
    {
        await using PluginDatabase database = await OpenAsync();
        await database.MigrateAsync(Schema);

        await database.ExecuteAsync(
            "INSERT INTO pieces (id, title) VALUES ($id, $title)",
            new Dictionary<string, object?> { ["$id"] = 1, ["$title"] = null }
        );

        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = await database.QueryAsync(
            "SELECT title FROM pieces"
        );

        rows.Single()["title"]
            .Should()
            .BeNull("DBNull reaching a plugin is a type it never asked for");
    }

    [Fact]
    public async Task Querying_answers_every_row_by_column_name()
    {
        await using PluginDatabase database = await OpenAsync();
        await database.MigrateAsync(Schema);
        await database.ExecuteAsync("INSERT INTO pieces (id, title) VALUES (1, 'a'), (2, 'b')");

        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = await database.QueryAsync(
            "SELECT id, title FROM pieces ORDER BY id"
        );

        rows.Should().HaveCount(2);
        rows[1]["title"].Should().Be("b");
        rows[1]["id"].Should().Be(2L);
    }

    [Fact]
    public async Task A_stream_that_is_stopped_early_reads_only_what_was_asked_for()
    {
        await using PluginDatabase database = await OpenAsync();
        await database.MigrateAsync(Schema);

        for (int id = 1; id <= 200; id++)
            await database.ExecuteAsync(
                "INSERT INTO pieces (id, title) VALUES ($id, 'row')",
                new Dictionary<string, object?> { ["$id"] = id }
            );

        List<long> seen = [];

        await foreach (
            IReadOnlyDictionary<string, object?> row in database.QueryStreamAsync(
                "SELECT id FROM pieces ORDER BY id"
            )
        )
        {
            seen.Add((long)row["id"]!);

            if (seen.Count == 3)
                break;
        }

        seen.Should().Equal(1L, 2L, 3L);

        (await database.ScalarAsync("SELECT COUNT(*) FROM pieces"))
            .Should()
            .Be(200L, "the reader was closed when enumeration stopped, so the file is usable");
    }

    [Fact]
    public async Task A_scalar_with_nothing_to_answer_is_null()
    {
        await using PluginDatabase database = await OpenAsync();
        await database.MigrateAsync(Schema);

        (await database.ScalarAsync("SELECT title FROM pieces WHERE id = 99")).Should().BeNull();
    }

    [Fact]
    public async Task The_facade_opens_the_database_inside_the_plugins_own_folder()
    {
        PluginHostStorage storage = Storage();

        await using (IPluginDatabase database = await storage.OpenDatabaseAsync("state"))
        {
            await database.MigrateAsync(Schema);
        }

        File.Exists(Path.Combine(_root, "data", Plugin.ToString(), "state.sqlite"))
            .Should()
            .BeTrue();
    }

    [Theory]
    [InlineData("../../library")]
    [InlineData("..\\library")]
    [InlineData("nested/state")]
    public async Task A_name_that_is_a_path_refuses(string name)
    {
        PluginHostStorage storage = Storage();

        PluginRefusedException refused = await Assert.ThrowsAsync<PluginRefusedException>(() =>
            storage.OpenDatabaseAsync(name)
        );

        refused
            .Refusal.Code.Should()
            .Be(
                PluginRefusalCodes.FileOutsideGrant,
                "a name that walks out of the folder could name the server's own database"
            );
    }

    private sealed class StubCatalog : IPluginFolderCatalog
    {
        public Task<IReadOnlyList<PluginStorageLocation>> LocationsAsync(
            CancellationToken ct = default
        ) => Task.FromResult<IReadOnlyList<PluginStorageLocation>>([]);

        public Task<IPluginStorageScope?> OpenAsync(
            string locationId,
            CancellationToken ct = default
        ) => Task.FromResult<IPluginStorageScope?>(null);
    }
}
