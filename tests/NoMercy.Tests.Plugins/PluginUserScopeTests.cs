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
using Xunit;

namespace NoMercy.Tests.Plugins;

public class PluginUserScopeTests
{
    [Fact]
    public void Storage_offers_a_scope_that_belongs_to_the_caller()
    {
        typeof(IPluginStorage)
            .GetProperty("ForUser")!
            .PropertyType.Should()
            .Be(typeof(IPluginUserScope));
    }

    [Fact]
    public void The_scope_exports_itself_as_json_the_host_can_hand_over()
    {
        typeof(IPluginUserScope)
            .GetMethod("ExportAsync")!
            .ReturnType.Should()
            .Be(typeof(Task<PluginUserScopeExport>));
    }

    [Fact]
    public void An_export_carries_who_and_when_so_it_can_be_checked_afterwards()
    {
        PluginUserScopeExport export = new()
        {
            Plugin = new(Ulid.Parse("01SAMPLE000000000000000001")),
            User = new(Ulid.Parse("248H248H248H248H248H248HAA")),
            TakenAt = DateTimeOffset.Parse("2026-09-20T10:00:00Z"),
            Json = "{}",
        };

        export
            .Files.Should()
            .BeEmpty("an export with no files is not an export with unknown files");
        export.Json.Should().Be("{}");
        export.TakenAt.Should().Be(DateTimeOffset.Parse("2026-09-20T10:00:00Z"));
    }

    [Fact]
    public void The_scope_purges_itself_when_the_user_leaves()
    {
        typeof(IPluginUserScope).GetMethod("PurgeAsync").Should().NotBeNull();
    }

    [Fact]
    public void The_scope_holds_files_and_a_database_of_its_own()
    {
        typeof(IPluginUserScope)
            .GetProperty("Files")!
            .PropertyType.Should()
            .Be(typeof(IPluginStorageScope));
        typeof(IPluginUserScope)
            .GetMethod("OpenDatabaseAsync")!
            .ReturnType.Should()
            .Be(typeof(Task<IPluginDatabase>));
    }

    [Fact]
    public void Every_user_capability_is_named_so_the_scope_covers_all_of_them()
    {
        IEnumerable<string> userCapabilities = PluginCapabilityVocabulary
            .All.Where(capability => capability.Name.StartsWith("user.", StringComparison.Ordinal))
            .Select(capability => capability.Name);

        userCapabilities
            .Should()
            .BeEquivalentTo(["user.identity", "user.watch", "user.playlists", "user.preferences"]);
    }

    [Fact]
    public void Holding_user_data_outside_the_scope_refuses()
    {
        PluginRefusal refusal = PluginRefusalMessages.UserScopeRequired(
            "Internet Radio 1.2.1",
            "favorites.json"
        );

        refusal.Code.Should().Be(PluginRefusalCodes.UserScopeRequired);
        refusal.Fix.Should().Contain("context.Storage.ForUser");
        refusal.What.Should().Contain("favorites.json");
    }

    [Fact]
    public void Sending_user_data_off_the_server_refuses_and_says_there_is_no_capability()
    {
        PluginRefusal refusal = PluginRefusalMessages.UserDataEgress(
            "Internet Radio 1.2.1",
            "analytics.example"
        );

        refusal.Code.Should().Be(PluginRefusalCodes.UserDataEgress);
        refusal.Why.Should().Contain("no capability");
        refusal
            .Severity.Should()
            .Be(
                PluginRefusalSeverity.Blocked,
                "a degraded refusal would let the data leave while the owner reads a warning"
            );
    }
}
