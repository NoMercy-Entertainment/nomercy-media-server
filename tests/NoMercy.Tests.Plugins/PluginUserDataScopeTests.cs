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

using System.Text;
using System.Text.Json;
using FluentAssertions;
using NoMercy.Plugins.Abstractions;
using NoMercy.Plugins.Storage;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// What the per-user scope actually does, not what its interface says.
/// <para>
/// The shape tests next door proved the contract exists. These prove the
/// export can be handed to a person and the purge leaves nothing, which is
/// the whole reason the scope is owned by the host rather than the plugin.
/// </para>
/// </summary>
public class PluginUserDataScopeTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "nm-user-scope-" + Ulid.NewUlid()
    );

    private readonly Ulid _plugin = Ulid.NewUlid();

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);

        GC.SuppressFinalize(this);
    }

    private PluginUserDataScope ScopeFor(UserId user) => new(_plugin, user, _root);

    private static async Task WriteAsync(IPluginStorageScope scope, string path, string contents)
    {
        await using Stream stream = await scope.OpenWriteAsync(path, true);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(contents));
    }

    [Fact]
    public async Task One_person_files_are_not_in_another_person_scope()
    {
        UserId first = new(Ulid.NewUlid());
        UserId second = new(Ulid.NewUlid());

        await WriteAsync(ScopeFor(first).Files, "favorites.json", "[\"kink\"]");

        bool leaked = await ScopeFor(second).Files.ExistsAsync("favorites.json");

        // A plugin that mixes users into one file can neither export nor
        // erase, and nobody finds out until somebody asks.
        leaked.Should().BeFalse();
    }

    [Fact]
    public async Task The_export_carries_what_the_plugin_holds_about_that_person()
    {
        UserId user = new(Ulid.NewUlid());
        PluginUserDataScope scope = ScopeFor(user);

        await WriteAsync(scope.Files, "favorites.json", "{\"stations\":[\"BBC\"]}");

        PluginUserScopeExport export = await scope.ExportAsync();

        export.User.Should().Be(user);
        export.Plugin.Value.Should().Be(_plugin);
        export.Files.Should().Contain("favorites.json");

        JsonElement parsed = JsonSerializer.Deserialize<JsonElement>(export.Json);

        parsed
            .GetProperty("favorites.json")
            .GetProperty("stations")[0]
            .GetString()
            .Should()
            .Be("BBC", "an export only the plugin can read answers on paper and not in fact");
    }

    [Fact]
    public async Task A_large_file_is_named_in_the_export_rather_than_inlined()
    {
        UserId user = new(Ulid.NewUlid());
        PluginUserDataScope scope = ScopeFor(user);

        await WriteAsync(scope.Files, "history.json", new string('x', 2 * 1024 * 1024));

        PluginUserScopeExport export = await scope.ExportAsync();

        // Building somebody's whole download history in memory before anybody
        // can read it is how an export request becomes an outage.
        export.Files.Should().Contain("history.json");
        export.Json.Should().NotContain("xxxxxxxxxx");
    }

    [Fact]
    public async Task A_file_the_plugin_named_json_and_wrote_as_something_else_still_travels()
    {
        UserId user = new(Ulid.NewUlid());
        PluginUserDataScope scope = ScopeFor(user);

        await WriteAsync(scope.Files, "notes.json", "not json at all");

        PluginUserScopeExport export = await scope.ExportAsync();

        // It is still that person's data. Dropping it from their own export
        // because the plugin author was careless is not their problem.
        JsonSerializer
            .Deserialize<JsonElement>(export.Json)
            .GetProperty("notes.json")
            .GetString()
            .Should()
            .Be("not json at all");
    }

    [Fact]
    public async Task A_purge_leaves_nothing_behind()
    {
        UserId user = new(Ulid.NewUlid());
        PluginUserDataScope scope = ScopeFor(user);

        await WriteAsync(scope.Files, "favorites.json", "[]");
        await WriteAsync(scope.Files, "deep/nested/thing.bin", "bytes");

        await scope.PurgeAsync();

        Directory
            .Exists(Path.Combine(_root, "users", user.Value.ToString()))
            .Should()
            .BeFalse(
                "\"mostly deleted\" is not a thing you can tell somebody who asked to be forgotten"
            );
    }

    [Fact]
    public async Task A_purge_does_not_touch_anybody_else()
    {
        UserId leaving = new(Ulid.NewUlid());
        UserId staying = new(Ulid.NewUlid());

        await WriteAsync(ScopeFor(leaving).Files, "favorites.json", "[]");
        await WriteAsync(ScopeFor(staying).Files, "favorites.json", "[]");

        await ScopeFor(leaving).PurgeAsync();

        bool stillThere = await ScopeFor(staying).Files.ExistsAsync("favorites.json");

        stillThere.Should().BeTrue();
    }

    [Fact]
    public async Task A_purge_leaves_the_plugin_own_files_alone()
    {
        UserId user = new(Ulid.NewUlid());
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "stations.db"), "plugin data");

        await WriteAsync(ScopeFor(user).Files, "favorites.json", "[]");
        await ScopeFor(user).PurgeAsync();

        // Per-user data lives under "users", so forgetting one person never
        // takes the plugin's own catalogue with them.
        File.Exists(Path.Combine(_root, "stations.db")).Should().BeTrue();
    }

    [Fact]
    public async Task A_purge_on_a_scope_nobody_wrote_to_is_not_an_error()
    {
        Func<Task> purge = () => ScopeFor(new UserId(Ulid.NewUlid())).PurgeAsync();

        // The host calls this when anybody leaves, including people who never
        // used the plugin. Throwing there would fail an account deletion.
        await purge.Should().NotThrowAsync();
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("nested/name")]
    [InlineData("back\\slash")]
    public async Task A_database_name_that_walks_out_of_the_folder_refuses(string name)
    {
        PluginUserDataScope scope = ScopeFor(new UserId(Ulid.NewUlid()));

        Func<Task> open = () => scope.OpenDatabaseAsync(name);

        // A name that escaped would be one person's scope reading another's.
        await open.Should().ThrowAsync<PluginRefusedException>();
    }

    [Fact]
    public async Task A_database_opened_in_the_scope_is_purged_with_it()
    {
        UserId user = new(Ulid.NewUlid());
        PluginUserDataScope scope = ScopeFor(user);

        await using (IPluginDatabase database = await scope.OpenDatabaseAsync("listens"))
        {
            await database.ExecuteAsync("CREATE TABLE plays (id TEXT PRIMARY KEY)");
        }

        await scope.PurgeAsync();

        // A database somewhere the host does not look is user data that
        // survives an erasure request.
        File.Exists(Path.Combine(_root, "users", user.Value.ToString(), "listens.sqlite"))
            .Should()
            .BeFalse();
    }
}
