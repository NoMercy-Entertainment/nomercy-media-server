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
using NoMercy.Plugins.Abstractions;
using NoMercy.Plugins.UserData;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// One folder per person per plugin, which is what makes an export and an
/// erasure possible at all. A plugin that mixed the two could do neither, and
/// nobody would find out until somebody asked.
/// </summary>
public class PluginUserDataScopeTests : IDisposable
{
    private static readonly Ulid Radio = Ulid.Parse("01J9ZK5V8Y0000000000000012");
    private static readonly Ulid Torrent = Ulid.Parse("01J9ZK5V8Y0000000000000013");
    private static readonly Guid Listener = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid Other = Guid.Parse("66666666-6666-6666-6666-666666666666");

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"plugin-user-data-{Ulid.NewUlid()}"
    );

    private PluginUserDataScope Scope(Ulid pluginId, Guid userId) => new(_root, pluginId, userId);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task What_a_person_stores_reads_back()
    {
        await Scope(Radio, Listener).SetAsync("last_station", "Radio Paradise");

        (await Scope(Radio, Listener).GetAsync<string>("last_station"))
            .Should()
            .Be("Radio Paradise");
    }

    [Fact]
    public async Task What_one_person_stores_is_not_visible_to_another()
    {
        await Scope(Radio, Listener).SetAsync("last_station", "Radio Paradise");

        (await Scope(Radio, Other).GetAsync<string>("last_station")).Should().BeNull();
    }

    [Fact]
    public async Task What_one_plugin_stores_is_not_visible_to_another()
    {
        await Scope(Radio, Listener).SetAsync("last_station", "Radio Paradise");

        (await Scope(Torrent, Listener).GetAsync<string>("last_station")).Should().BeNull();
    }

    [Fact]
    public async Task A_key_cannot_climb_out_of_the_folder()
    {
        await Scope(Radio, Listener).SetAsync("../../escaped", "somewhere else");

        Directory
            .EnumerateFiles(Scope(Radio, Listener).Folder)
            .Should()
            .ContainSingle("a key is a name, and a name that walks up is still a name");
    }

    [Fact]
    public async Task Removing_a_key_removes_it()
    {
        PluginUserDataScope scope = Scope(Radio, Listener);
        await scope.SetAsync("last_station", "Radio Paradise");

        await scope.DeleteAsync("last_station");

        (await scope.GetAsync<string>("last_station")).Should().BeNull();
    }

    [Fact]
    public void A_user_capability_called_with_nobody_asking_is_refused()
    {
        PluginRefusal refusal = PluginUserDataScope.Require(null, Radio)!;

        refusal.Code.Should().Be(PluginRefusalCodes.UserDataScopeRequired);
        refusal.Why.Should().Contain("per user");
    }

    [Fact]
    public void The_empty_account_is_nobody_asking()
    {
        PluginUserDataScope
            .Require(Guid.Empty, Radio)
            .Should()
            .NotBeNull("an unauthenticated caller and a scheduled job are the same nobody");
    }

    [Fact]
    public void Somebody_asking_is_not_refused()
    {
        PluginUserDataScope.Require(Listener, Radio).Should().BeNull();
    }

    [Fact]
    public async Task Export_returns_every_key_that_person_has_with_that_plugin()
    {
        PluginUserDataScope scope = Scope(Radio, Listener);
        await scope.SetAsync("last_station", "Radio Paradise");
        await scope.SetAsync("volume", 0.7);

        string json = await new PluginUserDataExporter(_root).ExportAsync(Radio, Listener);

        json.Should().Contain("last_station").And.Contain("volume").And.Contain("Radio Paradise");
    }

    [Fact]
    public async Task Export_leaves_out_what_belongs_to_somebody_else()
    {
        await Scope(Radio, Listener).SetAsync("last_station", "Radio Paradise");
        await Scope(Radio, Other).SetAsync("last_station", "Soma FM");

        string json = await new PluginUserDataExporter(_root).ExportAsync(Radio, Listener);

        json.Should().NotContain("Soma FM");
    }

    [Fact]
    public async Task Export_of_a_person_with_nothing_is_empty_rather_than_an_error()
    {
        string json = await new PluginUserDataExporter(_root).ExportAsync(Radio, Listener);

        json.Should().Be("{}");
    }

    [Fact]
    public async Task Purge_removes_everything_for_that_person_and_nothing_for_another()
    {
        await Scope(Radio, Listener).SetAsync("last_station", "Radio Paradise");
        await Scope(Radio, Other).SetAsync("last_station", "Soma FM");

        new PluginUserDataExporter(_root).Purge(Radio, Listener);

        (await Scope(Radio, Listener).GetAsync<string>("last_station")).Should().BeNull();
        (await Scope(Radio, Other).GetAsync<string>("last_station")).Should().Be("Soma FM");
    }

    [Fact]
    public async Task Purging_a_person_across_every_plugin_leaves_nothing_behind()
    {
        await Scope(Radio, Listener).SetAsync("a", 1);
        await Scope(Torrent, Listener).SetAsync("b", 2);
        await Scope(Torrent, Other).SetAsync("b", 3);

        new PluginUserDataExporter(_root).PurgeEverywhere(Listener);

        (await Scope(Radio, Listener).GetAsync<int?>("a")).Should().BeNull();
        (await Scope(Torrent, Listener).GetAsync<int?>("b")).Should().BeNull();
        (await Scope(Torrent, Other).GetAsync<int?>("b"))
            .Should()
            .Be(3, "one person leaving is not everybody leaving");
    }

    [Fact]
    public void Purging_a_server_that_has_stored_nothing_is_not_an_error()
    {
        Action purging = () => new PluginUserDataExporter(_root).PurgeEverywhere(Listener);

        purging.Should().NotThrow();
    }
}
