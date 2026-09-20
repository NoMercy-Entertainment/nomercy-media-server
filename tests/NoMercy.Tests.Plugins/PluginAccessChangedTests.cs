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
using NoMercy.Plugins.Access;
using NoMercy.Plugins.Capabilities;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// Everybody is told their own answer, one message each. A broadcast would
/// tell a household who paid for what, and telling only whoever changed leaves
/// a television showing something nobody can open.
/// </summary>
public class PluginAccessChangedTests
{
    private static readonly Ulid Radio = Ulid.Parse("01J9ZK5V8Y000000000000000C");
    private static readonly Ulid Torrent = Ulid.Parse("01J9ZK5V8Y000000000000000D");
    private static readonly Guid Owner = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Member = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static (PluginAccessNotifier Notifier, RecordingHub Hub) Build(
        PluginAccess owner,
        PluginAccess member
    )
    {
        RecordingHub hub = new();

        return (
            new(
                new StubResolver(owner, member),
                new StubMembership(Owner, Member),
                new StubPlugins(Radio, Torrent),
                hub,
                () => Owner
            ),
            hub
        );
    }

    [Fact]
    public void An_entitlement_arriving_tells_the_owner_and_every_member()
    {
        (PluginAccessNotifier notifier, RecordingHub hub) = Build(
            PluginAccess.Owned,
            PluginAccess.Shared
        );

        notifier.AccessMayHaveChanged(Radio);

        hub.Sent.Should().HaveCount(2);
        hub.Sent.Should().Contain(sent => sent.UserId == Owner && sent.Access == "owned");
        hub.Sent.Should().Contain(sent => sent.UserId == Member && sent.Access == "shared");
    }

    [Fact]
    public void A_revocation_tells_everybody_they_have_no_access()
    {
        (PluginAccessNotifier notifier, RecordingHub hub) = Build(
            PluginAccess.None,
            PluginAccess.None
        );

        notifier.AccessMayHaveChanged(Radio);

        hub.Sent.Should().HaveCount(2);
        hub.Sent.Should().OnlyContain(sent => sent.Access == "none");
    }

    [Fact]
    public void The_message_names_the_plugin_and_nothing_about_the_person()
    {
        (PluginAccessNotifier notifier, RecordingHub hub) = Build(
            PluginAccess.Owned,
            PluginAccess.None
        );

        notifier.AccessMayHaveChanged(Radio);

        hub.Sent[0].PluginId.Should().Be(Radio);
    }

    [Fact]
    public void A_guest_only_plugin_still_tells_the_owner_it_is_not_theirs()
    {
        (PluginAccessNotifier notifier, RecordingHub hub) = Build(
            PluginAccess.None,
            PluginAccess.Owned
        );

        notifier.AccessMayHaveChanged(Radio);

        hub.Sent.Single(sent => sent.UserId == Member).Access.Should().Be("owned");
        hub.Sent.Single(sent => sent.UserId == Owner)
            .Access.Should()
            .Be("none", "a client that is not told stays showing what it last saw");
    }

    [Fact]
    public void A_list_arriving_answers_for_every_plugin()
    {
        (PluginAccessNotifier notifier, RecordingHub hub) = Build(
            PluginAccess.Owned,
            PluginAccess.Shared
        );

        notifier.EverythingMayHaveChanged();

        hub.Sent.Select(sent => sent.PluginId)
            .Distinct()
            .Should()
            .BeEquivalentTo(
                [Radio, Torrent],
                "a revocation list names what it blocks, never what it stopped blocking"
            );
    }

    [Fact]
    public void A_server_with_no_owner_tells_nobody()
    {
        RecordingHub hub = new();
        PluginAccessNotifier notifier = new(
            new StubResolver(PluginAccess.Owned, PluginAccess.Shared),
            new StubMembership(Guid.Empty),
            new StubPlugins(Radio),
            hub,
            () => Guid.Empty
        );

        notifier.AccessMayHaveChanged(Radio);

        hub.Sent.Should().BeEmpty();
    }

    private sealed class StubResolver(PluginAccess owner, PluginAccess member)
        : IPluginAccessResolver
    {
        public PluginAccess Resolve(Ulid pluginId, Guid userId) => userId == Owner ? owner : member;
    }

    private sealed class StubMembership(params Guid[] everyone) : IPluginMembership
    {
        public bool IsAcceptedMember(Guid userId) => everyone.Contains(userId);

        public IReadOnlyList<Guid> EveryoneOn(Guid ownerId) =>
            [.. everyone.Where(id => id != Guid.Empty)];

        public int SeatsTakenFor(Ulid pluginId) => 0;
    }

    private sealed class StubPlugins(params Ulid[] ids) : IPluginManifestSource
    {
        public PluginInfo? Find(Ulid pluginId) => All().FirstOrDefault(info => info.Id == pluginId);

        public IReadOnlyList<PluginInfo> All() =>
            [
                .. ids.Select(id => new PluginInfo
                {
                    Id = id,
                    Name = "Sample",
                    Description = "d",
                    Version = new(1, 0, 0),
                    Status = PluginStatus.Active,
                }),
            ];
    }

    private sealed class RecordingHub : IPluginAccessHub
    {
        public List<(Guid UserId, Ulid PluginId, string Access)> Sent { get; } = [];

        public void Send(Guid userId, Ulid pluginId, string access) =>
            Sent.Add((userId, pluginId, access));
    }
}
