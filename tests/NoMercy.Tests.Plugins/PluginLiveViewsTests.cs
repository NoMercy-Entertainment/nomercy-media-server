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

/// <summary>
/// Live television drawn out of the ordinary vocabulary.
///
/// <para>
/// The thing being pinned is that no component here belongs to live: a control
/// strip is a row of buttons and a guide is a table, so a tuner plugin reaches
/// every client without one of them learning a word about channels.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public class PluginLiveViewsTests
{
    private static readonly DateTimeOffset From = new(2026, 9, 16, 20, 0, 0, TimeSpan.Zero);

    private static readonly PluginGuideWindow Window = new(From, From.AddHours(2));

    private static PluginLiveDescriptor Channel(
        string channelId,
        int number,
        string name,
        bool startOver = false,
        bool record = false,
        IReadOnlyList<string>? qualities = null
    )
    {
        return new PluginLiveDescriptor(
            ChannelId: channelId,
            Number: number,
            Name: name,
            Group: null,
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

    private static PluginEpgProgramme Programme(
        string programmeId,
        string channelId,
        string title,
        int startsAtMinute,
        int endsAtMinute
    )
    {
        return new PluginEpgProgramme(
            programmeId,
            channelId,
            title,
            null,
            From.AddMinutes(startsAtMinute),
            From.AddMinutes(endsAtMinute),
            null,
            null,
            null
        );
    }

    private static PluginActionIntent Intent(string name)
    {
        return new PluginActionIntent
        {
            Type = "live",
            Payload = new() { ["action"] = name },
        };
    }

    private static readonly IReadOnlyList<PluginLiveDescriptor> Channels =
    [
        Channel("b", 101, "Hundred One"),
        Channel("a", 1, "One", startOver: true, qualities: ["auto"]),
        Channel("c", 50, "Fifty", record: true),
    ];

    private static readonly IReadOnlyList<PluginEpgProgramme> Programmes =
    [
        Programme("p2", "a", "Film", 30, 180),
        Programme("p1", "a", "News", -30, 30),
        Programme("p3", "b", "Other", 0, 60),
        Programme("p0", "a", "Yesterday", -24 * 60, -23 * 60),
    ];

    [Fact]
    public void Controls_AreButtonsAndNothingLiveKnows()
    {
        PluginComponent controls = PluginLiveViews.Controls("live", Channels[1], Intent);

        controls.Component.Should().Be(PluginComponentType.Row);
        controls.Items.Should().OnlyContain(item => item.Component == PluginComponentType.Button);
    }

    [Fact]
    public void Controls_DrawOnlyTheActionsTheChannelOffers()
    {
        PluginComponent offered = PluginLiveViews.Controls("live", Channels[1], Intent);
        PluginComponent bare = PluginLiveViews.Controls("live", Channels[0], Intent);

        offered.Items.Should().HaveCount(8);
        offered.Items.Should().Contain(item => item.Id.EndsWith(PluginLiveAction.StartOver));
        offered.Items.Should().Contain(item => item.Id.EndsWith(PluginLiveAction.Quality));

        bare.Items.Should().HaveCount(6);
        bare.Items.Should().NotContain(item => item.Id.EndsWith(PluginLiveAction.Record));
    }

    [Fact]
    public void EveryControl_CarriesThePluginsOwnAction()
    {
        PluginComponent controls = PluginLiveViews.Controls("live", Channels[1], Intent);

        controls.Items.Should().OnlyContain(item => item.Action != null);
        controls.Items.First().Action!.Payload["action"].Should().Be(PluginLiveAction.ChannelUp);
    }

    [Fact]
    public void TheStrip_FollowsTheNumbersNotTheOrderTheProviderWrote()
    {
        PluginComponent strip = PluginLiveViews.ChannelStrip(
            "strip",
            Channels,
            current: null,
            channel => Intent(channel.ChannelId)
        );

        strip
            .Items.Select(item => item.Id)
            .Should()
            .ContainInOrder("strip-a", "strip-c", "strip-b");
    }

    [Fact]
    public void TheStrip_MarksTheChannelThatIsPlaying()
    {
        PluginComponent strip = PluginLiveViews.ChannelStrip(
            "strip",
            Channels,
            current: "c",
            channel => Intent(channel.ChannelId)
        );

        strip.Items.Single(item => item.Id == "strip-c").Props["variant"].Should().Be("primary");
        strip.Items.Single(item => item.Id == "strip-a").Props["variant"].Should().Be("secondary");
    }

    [Fact]
    public void TheGuide_IsATableOfHalfHours()
    {
        PluginComponent guide = Guide();

        guide.Component.Should().Be(PluginComponentType.Table);
        guide.Items.Should().NotBeEmpty();
    }

    [Fact]
    public void TheGuide_HasOneRowPerChannelInNumberOrder()
    {
        IReadOnlyList<PluginGuideRow> rows = PluginGuideLayout.Layout(Channels, Programmes, Window);

        rows.Select(row => row.ChannelId).Should().ContainInOrder("a", "c", "b");
        rows.Should().HaveCount(3);
    }

    [Fact]
    public void AProgrammeRunningAcrossSlots_NamesItselfInEveryOneItCovers()
    {
        PluginComponent guide = Guide();

        CellText(guide, "guide-a", "t0").Should().Be("News");
        CellText(guide, "guide-a", "t30").Should().Be("Film");
        CellText(guide, "guide-a", "t60").Should().Be("Film");
        CellText(guide, "guide-a", "t90").Should().Be("Film");
    }

    [Fact]
    public void AChannelWithNothingScheduled_SaysSoRatherThanDrawingAnEmptyRow()
    {
        CellText(Guide(), "guide-c", "t0").Should().Be("Nothing scheduled");
    }

    [Fact]
    public void TheChannelColumn_NamesTheChannelByItsNumberAndItsName()
    {
        CellText(Guide(), "guide-b", "channel").Should().Be("101 Hundred One");
    }

    /// <summary>What a reader announces in one cell, after the table has drawn it.</summary>
    private static string CellText(PluginComponent guide, string rowId, string columnKey)
    {
        PluginComponent row = guide.Items.Single(item => item.Id == rowId);
        PluginComponent cell = row.Items.Single(item => item.Id == $"{rowId}-{columnKey}");

        return Text(cell) ?? string.Empty;
    }

    private static string? Text(PluginComponent node)
    {
        if (node.Props.TryGetValue("text", out object? value) && value is string text)
        {
            return text;
        }

        return node.Items.Select(Text).FirstOrDefault(found => found is not null);
    }

    [Fact]
    public void AProgrammeOutsideTheWindow_IsNotDrawnAtAll()
    {
        IReadOnlyList<PluginGuideItem> items = PluginGuideLayout
            .Layout(Channels, Programmes, Window)
            .Single(row => row.ChannelId == "a")
            .Items;

        items.Select(item => item.ProgrammeId).Should().ContainInOrder("p1", "p2");
        items.Should().NotContain(item => item.ProgrammeId == "p0");
        items.Should().NotContain(item => item.ProgrammeId == "p3");
    }

    [Fact]
    public void AProgrammeIsClippedToTheWindowItIsDrawnIn()
    {
        IReadOnlyList<PluginGuideItem> items = PluginGuideLayout
            .Layout(Channels, Programmes, Window)
            .Single(row => row.ChannelId == "a")
            .Items;

        items[0].OffsetMinutes.Should().Be(0);
        items[0].WidthMinutes.Should().Be(30);
        items[0].ClippedStart.Should().BeTrue();

        items[1].OffsetMinutes.Should().Be(30);
        items[1].WidthMinutes.Should().Be(90);
        items[1].ClippedStop.Should().BeTrue();
    }

    [Fact]
    public void TheNowLine_SitsWhereTheClockIsAndNowhereWhenItIsOutside()
    {
        PluginGuideLayout.NowOffset(Window, From.AddMinutes(45)).Should().Be(45);
        PluginGuideLayout.NowOffset(Window, From.AddMinutes(-60)).Should().BeNull();
        PluginGuideLayout.NowOffset(Window, From.AddMinutes(180)).Should().BeNull();
    }

    private static PluginComponent Guide()
    {
        return PluginLiveViews.Guide(
            "guide",
            Channels,
            Programmes,
            Window,
            "Channel",
            "Nothing scheduled",
            at => at.ToString("HH:mm")
        );
    }
}
