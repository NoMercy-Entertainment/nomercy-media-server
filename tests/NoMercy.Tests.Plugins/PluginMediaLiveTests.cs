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
using NoMercy.Plugins.Capabilities;
using NoMercy.Plugins.Media;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// The plugin knows the provider and stops being involved once it has
/// published. What a client is handed is a channel on this server, because a
/// provider's own address is where the credential lives.
/// </summary>
public class PluginMediaLiveTests
{
    private static readonly Ulid Iptv = Ulid.Parse("01J9ZK5V8Y0000000000000003");

    private static readonly DateTimeOffset Evening = new(2026, 9, 16, 20, 0, 0, TimeSpan.Zero);

    private static PluginLiveChannel Channel(string id = "bbc-one", params string[] urls) =>
        new()
        {
            Id = id,
            Name = "BBC One",
            Number = 101,
            Group = "UK",
            StreamKind = PluginLiveStreamKind.Hls,
            Links =
            [
                .. (urls.Length == 0 ? ["https://a.example.com/one.m3u8"] : urls).Select(
                    url => new PluginProxyLink { Url = new(url) }
                ),
            ],
        };

    private static (PluginMediaLive Live, PluginLiveStore Store) Build(
        PluginRefusal? refusal = null,
        string? refuseCapability = null
    )
    {
        PluginLiveStore store = new();

        return (new(Iptv, new StubBroker(refusal, refuseCapability), store), store);
    }

    [Fact]
    public async Task Publishing_without_the_capability_is_refused()
    {
        // Scoped to media.live alone, so the fact fails when that check goes
        // and not because the link check happened to refuse as well.
        (PluginMediaLive live, _) = Build(
            Refusal(PluginRefusalCodes.CapabilityNotDeclared),
            refuseCapability: PluginCapabilityNames.MediaLive
        );

        Func<Task> act = () => live.PublishAsync([Channel()]);

        (await act.Should().ThrowAsync<PluginRefusedException>())
            .Which.Refusal.Code.Should()
            .Be(PluginRefusalCodes.CapabilityNotDeclared);
    }

    [Fact]
    public async Task A_channel_naming_a_host_outside_the_scope_is_refused()
    {
        (PluginMediaLive live, PluginLiveStore store) = Build(
            Refusal(PluginRefusalCodes.CapabilityScopeRefused),
            refuseCapability: PluginCapabilityNames.MediaProxy
        );

        Func<Task> act = () =>
            live.PublishAsync([Channel(urls: "https://elsewhere.example/x.m3u8")]);

        await act.Should().ThrowAsync<PluginRefusedException>();
        store
            .Channels(Iptv)
            .Should()
            .BeEmpty(
                "a list holding an address the manifest never named is one the server serves from"
            );
    }

    [Fact]
    public async Task Every_link_of_a_channel_is_checked_not_only_the_first()
    {
        CountingBroker broker = new();
        PluginLiveStore store = new();
        PluginMediaLive live = new(Iptv, broker, store);

        await live.PublishAsync([
            Channel(urls: ["https://a.example.com/one.m3u8", "https://b.example.com/one.m3u8"]),
        ]);

        broker.Hosts.Should().Equal(["a.example.com", "b.example.com"]);
    }

    [Fact]
    public async Task Published_channels_keep_their_number_and_group()
    {
        (PluginMediaLive live, PluginLiveStore store) = Build();

        await live.PublishAsync([Channel()]);

        PluginLiveChannel published = store.Channels(Iptv).Should().ContainSingle().Subject;
        published.Number.Should().Be(101);
        published.Group.Should().Be("UK");
    }

    [Fact]
    public async Task A_radio_channel_stays_marked_for_the_audio_player()
    {
        (PluginMediaLive live, PluginLiveStore store) = Build();

        await live.PublishAsync([
            Channel() with
            {
                Radio = true,
                StreamKind = PluginLiveStreamKind.ContinuousTs,
            },
        ]);

        store.Channels(Iptv)[0].Radio.Should().BeTrue();
    }

    [Fact]
    public async Task An_adult_channel_carries_its_flag_through()
    {
        (PluginMediaLive live, PluginLiveStore store) = Build();

        await live.PublishAsync([Channel() with { Adult = true }]);

        store.Channels(Iptv)[0].Adult.Should().BeTrue();
    }

    [Fact]
    public async Task Publishing_again_replaces_the_list_rather_than_adding_to_it()
    {
        (PluginMediaLive live, PluginLiveStore store) = Build();
        await live.PublishAsync([Channel("bbc-one"), Channel("itv")]);

        await live.PublishAsync([Channel("bbc-one")]);

        store
            .Channels(Iptv)
            .Should()
            .ContainSingle("a channel the provider dropped must not stay in the guide forever");
    }

    [Fact]
    public async Task One_channel_is_found_by_its_id_whatever_case_it_is_asked_in()
    {
        (PluginMediaLive live, PluginLiveStore store) = Build();
        await live.PublishAsync([Channel("bbc-one")]);

        store.Channel(Iptv, "BBC-One").Should().NotBeNull();
        store.Channel(Iptv, "itv").Should().BeNull();
    }

    [Fact]
    public async Task One_plugins_channels_are_not_anothers()
    {
        (PluginMediaLive live, PluginLiveStore store) = Build();
        await live.PublishAsync([Channel()]);

        store.Channels(Ulid.NewUlid()).Should().BeEmpty();
    }

    [Fact]
    public async Task Groups_keep_the_order_the_provider_gave()
    {
        (PluginMediaLive live, PluginLiveStore store) = Build();

        await live.PublishGroupsAsync([
            new()
            {
                Name = "UK",
                ChannelIds = ["bbc-one", "itv"],
                SortOrder = 1,
            },
            new()
            {
                Name = "Sport",
                ChannelIds = ["sky-sports"],
                SortOrder = 2,
            },
        ]);

        store.Groups(Iptv).Select(group => group.Name).Should().Equal(["UK", "Sport"]);
        store.Groups(Iptv)[0].ChannelIds.Should().Equal(["bbc-one", "itv"]);
    }

    [Fact]
    public async Task Publishing_groups_without_the_capability_is_refused()
    {
        (PluginMediaLive live, _) = Build(
            Refusal(PluginRefusalCodes.CapabilityNotDeclared),
            refuseCapability: PluginCapabilityNames.MediaLive
        );

        Func<Task> act = () => live.PublishGroupsAsync([]);

        await act.Should().ThrowAsync<PluginRefusedException>();
    }

    [Fact]
    public async Task The_guide_answers_what_is_on_now()
    {
        (PluginMediaLive live, PluginLiveStore store) = Build();

        await live.PublishGuideAsync([
            Program("bbc-one", "The News", Evening, Evening.AddMinutes(30)),
            Program("bbc-one", "The Film", Evening.AddMinutes(30), Evening.AddHours(2)),
        ]);

        store
            .ProgramsAt(Iptv, "bbc-one", Evening.AddMinutes(10))
            .Should()
            .ContainSingle()
            .Which.Title.Should()
            .Be("The News");
    }

    [Fact]
    public async Task A_program_that_stops_exactly_now_is_the_one_that_just_ended()
    {
        (PluginMediaLive live, PluginLiveStore store) = Build();

        await live.PublishGuideAsync([
            Program("bbc-one", "The News", Evening, Evening.AddMinutes(30)),
        ]);

        store.ProgramsAt(Iptv, "bbc-one", Evening.AddMinutes(30)).Should().BeEmpty();
        store.ProgramsAt(Iptv, "bbc-one", Evening).Should().ContainSingle();
    }

    [Fact]
    public async Task The_guide_for_one_channel_is_not_anothers()
    {
        (PluginMediaLive live, PluginLiveStore store) = Build();

        await live.PublishGuideAsync([
            Program("bbc-one", "The News", Evening, Evening.AddMinutes(30)),
        ]);

        store.ProgramsAt(Iptv, "itv", Evening.AddMinutes(10)).Should().BeEmpty();
    }

    [Fact]
    public async Task Publishing_a_guide_without_the_capability_is_refused()
    {
        (PluginMediaLive live, _) = Build(
            Refusal(PluginRefusalCodes.CapabilityNotDeclared),
            refuseCapability: PluginCapabilityNames.MediaLive
        );

        Func<Task> act = () => live.PublishGuideAsync([]);

        await act.Should().ThrowAsync<PluginRefusedException>();
    }

    private static PluginEpgProgram Program(
        string channelId,
        string title,
        DateTimeOffset start,
        DateTimeOffset stop
    ) =>
        new()
        {
            ChannelId = channelId,
            Title = title,
            Start = start,
            Stop = stop,
        };

    private static PluginRefusal Refusal(string code) =>
        new(code, "plugin", "what", "why", "fix", PluginRefusalSeverity.Blocked);

    private sealed class StubBroker(PluginRefusal? refusal, string? onlyFor)
        : IPluginCapabilityBroker
    {
        public PluginRefusal? Check(Ulid pluginId, string capability, string? scope = null) =>
            onlyFor is null || onlyFor == capability ? refusal : null;
    }

    private sealed class CountingBroker : IPluginCapabilityBroker
    {
        public List<string> Hosts { get; } = [];

        public PluginRefusal? Check(Ulid pluginId, string capability, string? scope = null)
        {
            if (capability == PluginCapabilityNames.MediaProxy && scope is not null)
                Hosts.Add(scope);

            return null;
        }
    }
}
