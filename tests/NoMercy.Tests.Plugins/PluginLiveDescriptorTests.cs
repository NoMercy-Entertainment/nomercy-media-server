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
using NoMercy.PluginSdk.Abstractions;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// What a live channel offers, and the order its upstreams are tried in. A
/// player that assumed every channel offered everything drew two buttons that
/// did nothing on most of them.
/// </summary>
[Trait("Category", "Unit")]
public class PluginLiveDescriptorTests
{
    private static PluginLiveDescriptor Channel(
        bool startOver = false,
        bool record = false,
        IReadOnlyList<string>? qualities = null
    )
    {
        return new PluginLiveDescriptor(
            ChannelId: "a",
            Number: 1,
            Name: "One",
            Group: "General",
            Logo: null,
            Adult: false,
            MediaKind: PluginLiveDescriptor.Hls,
            Links: [],
            CatchUp: false,
            StartOver: startOver,
            Record: record,
            Qualities: qualities ?? [],
            Guide: []
        );
    }

    [Fact]
    public void EveryChannel_OffersTheSixActionsEveryRemoteHas()
    {
        IReadOnlyList<string> actions = Channel().SupportedActions();

        actions
            .Should()
            .BeEquivalentTo([
                PluginLiveAction.ChannelUp,
                PluginLiveAction.ChannelDown,
                PluginLiveAction.ChannelNumber,
                PluginLiveAction.LastChannel,
                PluginLiveAction.MiniGuide,
                PluginLiveAction.JumpToLive,
            ]);
    }

    [Fact]
    public void AChannel_OffersOnlyWhatItDeclares()
    {
        IReadOnlyList<string> actions = Channel(
                startOver: true,
                record: true,
                qualities: ["auto", "720p"]
            )
            .SupportedActions();

        actions.Should().Contain(PluginLiveAction.StartOver);
        actions.Should().Contain(PluginLiveAction.Record);
        actions.Should().Contain(PluginLiveAction.Quality);
    }

    [Fact]
    public void AChannelWithNoQualities_DoesNotOfferAQualityMenu()
    {
        Channel(qualities: []).SupportedActions().Should().NotContain(PluginLiveAction.Quality);
    }

    [Fact]
    public void EveryActionAChannelOffers_IsOneTheVocabularyNames()
    {
        IReadOnlyList<string> actions = Channel(startOver: true, record: true, qualities: ["auto"])
            .SupportedActions();

        actions.Should().OnlyContain(action => PluginLiveAction.IsKnown(action));
    }

    [Fact]
    public void Links_AreTriedLowestPriorityFirst()
    {
        PluginLiveDescriptor channel = Channel() with
        {
            Links =
            [
                new PluginLiveLink(3, new Uri("https://c.test/s"), "ts", null, null, null),
                new PluginLiveLink(1, new Uri("https://a.test/s"), "ts", null, null, 2),
                new PluginLiveLink(2, new Uri("https://b.test/s"), "ts", null, null, null),
            ],
        };

        channel
            .LinksInOrder.Select(link => link.Url.Host)
            .Should()
            .ContainInOrder("a.test", "b.test", "c.test");
    }

    [Fact]
    public void TheMediaKinds_AreTheThreeAClientCanPlay()
    {
        PluginLiveDescriptor.MediaKinds.Should().BeEquivalentTo(["hls", "ts", "radio"]);
    }
}
