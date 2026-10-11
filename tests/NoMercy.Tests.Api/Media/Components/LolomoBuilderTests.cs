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

using NoMercy.Api.DTOs.Media.Components;
using Xunit;

namespace NoMercy.Tests.Api.Media.Components;

[Trait("Category", "Unit")]
public class LolomoBuilderTests
{
    private static ComponentEnvelope Row(string id, int cards, string? next, string? previous) =>
        Component
            .Carousel()
            .WithId(id)
            .WithTitle(id)
            .WithNavigation(previous, next)
            .WithItems(
                Enumerable
                    .Range(0, cards)
                    .Select(_ => Component.Card().WithData(new CardData { Title = id }))
            )
            .Build();

    private static LolomoProps Props(ComponentEnvelope envelope) =>
        Assert.IsType<LolomoProps>(envelope.Props);

    private static ContainerProps Row(ComponentEnvelope envelope) =>
        Assert.IsAssignableFrom<ContainerProps>(envelope.Props);

    [Fact]
    public void Build_IsOneNmLolomo_WithThePageId()
    {
        ComponentEnvelope envelope = Component
            .Lolomo("genre-carousels")
            .WithItems([Row("A", 1, null, null)]);

        Assert.Equal("NMLolomo", envelope.Component);
        Assert.Equal("genre-carousels", (string)Props(envelope).Id);
    }

    [Fact]
    public void Build_DropsRowsThatAreNotCarousels()
    {
        ComponentEnvelope grid = Component
            .Grid()
            .WithId("g")
            .WithItems(Component.Card().WithData(new CardData { Title = "x" }).Build())
            .Build();
        ComponentEnvelope hero = Component.HomeCard(new HomeCardData()).Build();

        ComponentEnvelope envelope = Component
            .Lolomo("home")
            .WithItems([hero, Row("A", 1, null, null), grid]);

        Assert.Equal(["A"], Props(envelope).Items.Select(row => (string)Row(row).Id));
        Assert.All(Props(envelope).Items, row => Assert.Equal("NMCarousel", row.Component));
    }

    [Fact]
    public void Build_DropsEmptyRows()
    {
        ComponentEnvelope envelope = Component
            .Lolomo("p")
            .WithItems([Row("A", 1, "B", null), Row("B", 0, "C", "A"), Row("C", 2, null, "B")]);

        Assert.Equal(["A", "C"], Props(envelope).Items.Select(row => (string)Row(row).Id));
    }

    [Fact]
    public void Build_RelinksNextAndPreviousOverTheKeptRows()
    {
        ComponentEnvelope envelope = Component
            .Lolomo("p")
            .WithItems([Row("A", 1, "B", null), Row("B", 0, "C", "A"), Row("C", 2, null, "B")]);

        List<ContainerProps> rows = [.. Props(envelope).Items.Select(Row)];

        Assert.Null((string?)rows[0].PreviousId);
        Assert.Equal("C", (string?)rows[0].NextId);
        Assert.Equal("A", (string?)rows[1].PreviousId);
        Assert.Null((string?)rows[1].NextId);
    }

    [Fact]
    public void Build_FlattensRowsHeldInAContainer()
    {
        ComponentEnvelope container = Component
            .Container()
            .WithId("c")
            .WithItems([Row("A", 1, null, null), Row("B", 1, null, null)])
            .Build();

        ComponentEnvelope envelope = Component.Lolomo("p").WithItems([container]);

        Assert.Equal(["A", "B"], Props(envelope).Items.Select(row => (string)Row(row).Id));
    }

    [Fact]
    public void Build_Hero_DefaultsToFocus_AndReadsUnknownAsFocus()
    {
        Assert.Equal("focus", Props(Component.Lolomo("p").WithItems([])).Hero);
        Assert.Equal("none", Props(Component.Lolomo("p").WithHero("none").WithItems([])).Hero);
        Assert.Equal("focus", Props(Component.Lolomo("p").WithHero("wide").WithItems([])).Hero);
    }
}
