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

using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NoMercy.Api.DTOs.Media.Components;
using NoMercy.Api.Plugins;
using NoMercy.NmSystem.Domain;
using NoMercy.PluginSdk.Abstractions;
using Xunit;

namespace NoMercy.Tests.Api.Plugins;

[Trait("Category", "Unit")]
public class PluginShelfCapsTests
{
    private static readonly JsonSerializerSettings ApiSettings =
        new MvcNewtonsoftJsonOptions().SerializerSettings;

    [Fact]
    public void The_sdk_caps_are_the_home_page_caps()
    {
        PluginPaging.MaximumRowCards.Should().Be(UiLimits.MaximumCardsInCarousel);
        PluginPaging.MaximumPageSize.Should().Be(UiLimits.MaximumItemsPerPage);
        PluginPaging.DefaultPageSize.Should().Be(UiLimits.MaximumCardsInCarousel);
    }

    [Fact]
    public void A_hand_written_grid_over_the_cap_is_trimmed_on_the_server()
    {
        PluginView view = PluginViews.Declarative(
            HandWritten("grid", "NMGrid", PluginPaging.MaximumPageSize + 7)
        );

        PluginView sent = PluginShelfCaps.Apply(view, PluginPageRequest.First, out int trimmed);

        trimmed.Should().Be(7);
        Items(sent.Components![0]).Should().HaveCount(PluginPaging.MaximumPageSize);
        Items(view.Components![0])
            .Should()
            .HaveCount(PluginPaging.MaximumPageSize + 7, "the plugin's own tree is not changed");
    }

    [Fact]
    public void A_hand_written_row_and_page_are_trimmed_to_theirs()
    {
        PluginComponent row = HandWritten("row", "NMCarousel", 50);
        PluginComponent page = new()
        {
            Id = "page",
            Component = "NMLolomo",
            Props = new() { ["id"] = "page" },
            Items = [.. Enumerable.Range(0, 40).Select(index => HandWritten($"r{index}", "NMCarousel", 1))],
        };

        PluginView sent = PluginShelfCaps.Apply(
            PluginViews.Declarative(PluginViews.Container("wrap", row), page),
            PluginPageRequest.First,
            out int trimmed
        );

        trimmed.Should().Be(14 + 4);
        Items(Items(sent.Components![0])[0]).Should().HaveCount(PluginPaging.MaximumRowCards);
        Items(sent.Components[1]).Should().HaveCount(PluginPaging.MaximumPageRows);
    }

    [Fact]
    public void A_normal_view_is_sent_as_it_was_written()
    {
        PluginView view = PluginViews.Declarative(
            PluginViews.Container(
                "a",
                PluginViews.Text("b", "t", "title"),
                PluginViews.Button("c", "Go", PluginActionIntent.RefreshView()),
                PluginShelves.Grid("g", [Card("x")])
            )
        );
        view.Layout = PluginLayout.Grid;

        PluginView sent = PluginShelfCaps.Apply(view, PluginPageRequest.First, out int trimmed);

        trimmed.Should().Be(0);
        Json(sent).Should().Be(Json(view));
    }

    [Fact]
    public void An_append_is_answered_with_its_one_shelf_trimmed_to_the_limit_asked_for()
    {
        PluginView view = PluginViews.Declarative(
            PluginViews.Text("heading", "Stations", "title"),
            PluginViews.Container("wrap", HandWritten("all", "NMGrid", 30))
        );

        PluginView sent = PluginShelfCaps.Apply(
            view,
            PluginPageRequest.From(
                new Dictionary<string, string> { ["append"] = "all", ["limit"] = "12" }
            ),
            out int trimmed
        );

        sent.Components.Should().ContainSingle().Which.Id.Should().Be("all");
        Items(sent.Components![0]).Should().HaveCount(12);
        trimmed.Should().Be(18);
    }

    [Fact]
    public void An_append_for_a_shelf_the_plugin_did_not_send_is_an_empty_view_meaning_no_more()
    {
        PluginView sent = PluginShelfCaps.Apply(
            PluginViews.Declarative(HandWritten("other", "NMGrid", 2)),
            PluginPageRequest.From(new Dictionary<string, string> { ["append"] = "all" }),
            out _
        );

        sent.Components.Should().BeEmpty();
    }

    [Fact]
    public void Every_property_of_a_view_survives_the_caps()
    {
        // A property added to PluginView later that Apply does not copy would
        // vanish from every plugin page. This lists them so that cannot happen quietly.
        typeof(PluginView)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .Should()
            .BeEquivalentTo(["Components", "WebView", "Layout", "RefreshInterval"]);

        PluginView view = new()
        {
            Components = [],
            WebView = new() { EntryUrl = "https://plugin.local/" },
            Layout = PluginLayout.Wide,
            RefreshInterval = 30,
        };

        PluginView sent = PluginShelfCaps.Apply(view, PluginPageRequest.First, out _);

        sent.WebView.Should().BeSameAs(view.WebView);
        sent.Layout.Should().Be(PluginLayout.Wide);
        sent.RefreshInterval.Should().Be(30);
    }

    [Fact]
    public void A_plugin_row_carries_the_keys_the_server_home_row_carries()
    {
        ComponentEnvelope server = Component
            .Carousel()
            .WithId("rock")
            .WithTitle("Rock")
            .WithMoreLink("/music/genres/rock")
            .WithNavigation("pop", "jazz")
            .WithItems(Component.Card().WithData(new CardData { Title = "x" }).Build())
            .Build();

        PluginComponent page = PluginShelves.Page(
            "p",
            [Shelf("pop"), Shelf("rock", moreLink: "/music/genres/rock"), Shelf("jazz")]
        );

        JObject serverProps = JObject.Parse(Json(server))["props"]!.Value<JObject>()!;
        JObject pluginRow = JObject.Parse(Json(page))["props"]!["items"]![1]!.Value<JObject>()!;

        pluginRow["component"]!.Value<string>().Should().Be(server.Component);
        Keys(pluginRow["props"]!.Value<JObject>()!)
            .Except(["index"])
            .Should()
            .BeEquivalentTo(Keys(serverProps));
        pluginRow["props"]!["previous_id"]!.Value<string>().Should().Be("pop");
        pluginRow["props"]!["next_id"]!.Value<string>().Should().Be("jazz");
    }

    [Fact]
    public void A_plugin_page_carries_the_keys_the_server_lolomo_carries()
    {
        ComponentEnvelope server = Component
            .Lolomo("home")
            .WithItems(
                [
                    Component
                        .Carousel()
                        .WithId("a")
                        .WithTitle("A")
                        .WithItems(Component.Card().WithData(new CardData { Title = "x" }).Build())
                        .Build(),
                ]
            );

        JObject serverJson = JObject.Parse(Json(server));
        JObject pluginJson = JObject.Parse(Json(PluginShelves.Page("home", [Shelf("a")])));

        pluginJson["component"]!.Value<string>().Should().Be(serverJson["component"]!.Value<string>());
        Keys(pluginJson["props"]!.Value<JObject>()!)
            .Should()
            .BeEquivalentTo(Keys(serverJson["props"]!.Value<JObject>()!));
        pluginJson["props"]!["hero"]!.Value<string>()
            .Should()
            .Be(serverJson["props"]!["hero"]!.Value<string>());
    }

    private static PluginComponent HandWritten(string id, string component, int items) =>
        new()
        {
            Id = id,
            Component = component,
            Props = new()
            {
                ["id"] = id,
                ["items"] = Enumerable.Range(0, items).Select(index => Card($"{id}-{index}")).ToList(),
            },
        };

    private static PluginComponent Card(string id) =>
        PluginShelves.MusicCard(
            new()
            {
                Id = id,
                Name = id,
                Link = "/x",
                Description = null,
            }
        );

    private static PluginShelf Shelf(string id, string? moreLink = null) =>
        new()
        {
            Id = id,
            Title = id,
            MoreLink = moreLink,
            Items = [Card($"{id}-card")],
        };

    private static List<PluginComponent> Items(PluginComponent node) =>
        [.. (IEnumerable<PluginComponent>)node.Props["items"]!];

    private static IEnumerable<string> Keys(JObject json) =>
        json.Properties().Select(property => property.Name);

    private static string Json(object value) => JsonConvert.SerializeObject(value, ApiSettings);
}
