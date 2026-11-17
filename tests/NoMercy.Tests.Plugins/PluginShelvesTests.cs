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
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using NoMercy.PluginSdk.Abstractions;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// The shelves as the client parses them. Every key here is one the app's own
/// grid, card, row or page reads, so these pin the exact JSON in
/// docs/plugins/shelves-and-paging.md.
/// </summary>
[Trait("Category", "Unit")]
public class PluginShelvesTests
{
    private static readonly JsonSerializerSettings ApiSettings = Settings();

    private static readonly PluginCaller AnyCaller = new(
        UserId.Empty,
        "Stoney",
        PluginRole.Owner,
        PluginAccess.Owned,
        "en",
        PluginSurface.Web
    );

    [Fact]
    public void A_music_card_writes_its_id_on_the_props_and_in_data_and_its_description()
    {
        PluginComponent card = PluginShelves.MusicCard(
            new()
            {
                Id = "station-1",
                Name = "Groove Salad",
                Link = "/music/plugins/radio/stations/1",
                Cover = "data:image/png;base64,AA==",
                Type = "artist",
                Description = "Ambient · US",
            }
        );

        Json(card)
            .Should()
            .Be(
                """{"id":"station-1","component":"NMMusicCard","props":{"id":"station-1","data":{"id":"station-1","name":"Groove Salad","link":"/music/plugins/radio/stations/1","cover":"data:image/png;base64,AA==","type":"artist","description":"Ambient · US"},"contextMenuItems":[]}}"""
            );
    }

    [Fact]
    public void A_music_card_with_no_description_says_so_rather_than_leaving_the_key_out()
    {
        JObject card = Parse(
            PluginShelves.MusicCard(
                new()
                {
                    Id = "a",
                    Name = "A",
                    Link = "/a",
                    Description = null,
                }
            )
        );

        card["props"]!["data"]!.Value<JObject>()!.ContainsKey("description").Should().BeTrue();
        card["props"]!["data"]!["description"]!.Type.Should().Be(JTokenType.Null);
    }

    [Fact]
    public void A_grid_writes_its_id_on_the_props_and_draws_its_cards_inside_them()
    {
        PluginComponent grid = PluginShelves.Grid("all", [Tile("a"), Tile("b")]);

        JObject json = Parse(grid);

        json["id"]!.Value<string>().Should().Be("all");
        json["component"]!.Value<string>().Should().Be("NMGrid");
        json["props"]!["id"]!.Value<string>().Should().Be("all");
        json["props"]!["items"]!
            .Select(item => item["id"]!.Value<string>())
            .Should()
            .Equal("a", "b");
        json["props"]!.Value<JObject>()!.ContainsKey("loadMore").Should().BeFalse();
    }

    [Fact]
    public void An_empty_grid_still_carries_an_items_list()
    {
        Json(PluginShelves.Grid("none", []))
            .Should()
            .Be("""{"id":"none","component":"NMGrid","props":{"id":"none","items":[]}}""");
    }

    [Fact]
    public void A_grid_keeps_no_more_than_the_page_cap()
    {
        PluginComponent grid = PluginShelves.Grid(
            "all",
            Enumerable
                .Range(0, PluginPaging.MaximumPageSize + 20)
                .Select(index => Tile($"c{index}"))
        );

        grid.Items.Should().HaveCount(PluginPaging.MaximumPageSize);
    }

    [Fact]
    public void A_grid_with_a_next_cursor_carries_a_load_more_for_the_same_view_and_query()
    {
        PluginViewRequest request = Request(
            "/search",
            new()
            {
                ["q"] = "jazz",
                ["limit"] = "24",
                ["surface"] = "web",
                ["features"] = "paging",
            }
        );

        JObject json = Parse(PluginShelves.Grid("results", [Tile("a")], request, "24"));

        JToken loadMore = json["props"]!["loadMore"]!;
        loadMore["type"]!.Value<string>().Should().Be("loadPage");
        loadMore["access"]!.Value<string>().Should().Be("Shared");
        loadMore.Value<JObject>()!.ContainsKey("confirm").Should().BeFalse();
        loadMore["payload"]!
            .ToString(Formatting.None)
            .Should()
            .Be(
                """{"route":"/search","target":"results","cursor":"24","limit":24,"mode":"append","query":{"q":"jazz"}}"""
            );
    }

    [Fact]
    public void A_next_cursor_without_the_request_to_page_from_is_refused()
    {
        Action build = () =>
            PluginShelves.Grid("all", [Tile("a")], request: null, nextCursor: "36");

        build.Should().Throw<ArgumentNullException>().WithMessage("*all*");
    }

    [Fact]
    public void A_row_writes_the_keys_the_home_page_row_reads()
    {
        PluginComponent row = PluginShelves.Row(
            new()
            {
                Id = "genre-rock",
                Title = "Rock",
                MoreLink = "/music/plugins/radio/genres/rock",
                MoreLinkText = "See all",
                Items = [Tile("a")],
            }
        );

        JObject json = Parse(row);

        json["component"]!.Value<string>().Should().Be("NMCarousel");
        json["props"]!
            .Value<JObject>()!
            .Properties()
            .Select(property => property.Name)
            .Should()
            .Equal("id", "title", "more_link", "more_link_text", "index", "items");
        json["props"]!["id"]!.Value<string>().Should().Be("genre-rock");
        json["props"]!["title"]!.Value<string>().Should().Be("Rock");
        json["props"]!["more_link"]!
            .Value<string>()
            .Should()
            .Be("/music/plugins/radio/genres/rock");
        json["props"]!["more_link_text"]!.Value<string>().Should().Be("See all");
        json["props"]!["index"]!.Value<int>().Should().Be(0);
    }

    [Fact]
    public void A_row_without_a_link_has_no_link_keys_and_keeps_its_cap()
    {
        PluginComponent row = PluginShelves.Row(
            new()
            {
                Id = "r",
                Title = "R",
                MoreLinkText = "ignored without a link",
                Items = [.. Enumerable.Range(0, 50).Select(index => Tile($"c{index}"))],
            }
        );

        JObject props = Parse(row)["props"]!.Value<JObject>()!;

        props.ContainsKey("more_link").Should().BeFalse();
        props.ContainsKey("more_link_text").Should().BeFalse();
        row.Items.Should().HaveCount(PluginPaging.MaximumRowCards);
    }

    [Fact]
    public void A_page_chains_its_rows_the_way_the_home_page_does_and_drops_empty_ones()
    {
        PluginComponent page = PluginShelves.Page(
            "radio-home",
            [Shelf("one"), Shelf("empty", cards: 0), Shelf("two"), Shelf("three")]
        );

        JObject json = Parse(page);
        json["component"]!.Value<string>().Should().Be("NMLolomo");
        json["props"]!["id"]!.Value<string>().Should().Be("radio-home");
        json["props"]!["hero"]!.Value<string>().Should().Be("focus");

        JToken[] rows = [.. json["props"]!["items"]!];
        rows.Select(row => row["id"]!.Value<string>()).Should().Equal("one", "two", "three");
        rows.Select(row => row["component"]!.Value<string>())
            .Should()
            .OnlyContain(name => name == "NMCarousel");
        rows.Select(row => row["props"]!["index"]!.Value<int>()).Should().Equal(0, 1, 2);

        rows[0]["props"]!.Value<JObject>()!.ContainsKey("previous_id").Should().BeFalse();
        rows[0]["props"]!["next_id"]!.Value<string>().Should().Be("two");
        rows[1]["props"]!["previous_id"]!.Value<string>().Should().Be("one");
        rows[1]["props"]!["next_id"]!.Value<string>().Should().Be("three");
        rows[2]["props"]!["previous_id"]!.Value<string>().Should().Be("two");
        rows[2]["props"]!.Value<JObject>()!.ContainsKey("next_id").Should().BeFalse();
    }

    [Fact]
    public void A_page_keeps_no_more_than_the_row_cap_and_can_hide_its_hero()
    {
        PluginComponent page = PluginShelves.Page(
            "p",
            Enumerable
                .Range(0, PluginPaging.MaximumPageRows + 4)
                .Select(index => Shelf($"r{index}")),
            hero: PluginShelves.HeroNone
        );

        page.Items.Should().HaveCount(PluginPaging.MaximumPageRows);
        page.Props["hero"].Should().Be("none");
    }

    [Fact]
    public void A_page_with_more_rows_carries_a_load_more_for_the_page()
    {
        PluginComponent page = PluginShelves.Page(
            "p",
            [Shelf("one")],
            Request("/", new() { ["cursor"] = "0" }),
            nextCursor: "1"
        );

        PluginActionIntent loadMore = (PluginActionIntent)page.Props["loadMore"]!;
        loadMore.Payload["target"].Should().Be("p");
        loadMore.Payload["cursor"].Should().Be("1");
        loadMore.Payload["mode"].Should().Be(PluginPaging.Append);
    }

    [Fact]
    public void Pagination_runs_a_replace_load_page_the_client_fills_with_the_chosen_page()
    {
        PluginComponent pagination = PluginShelves.Pagination(
            "pages",
            currentPage: 9,
            totalPages: 4,
            Request("/all", new() { ["limit"] = "50", ["cursor"] = "2" })
        );

        JObject json = Parse(pagination);

        json["component"]!.Value<string>().Should().Be("NMPagination");
        json["props"]!["currentPage"]!.Value<int>().Should().Be(4);
        json["props"]!["totalPages"]!.Value<int>().Should().Be(4);
        json["props"]!["action"]!["payload"]!
            .ToString(Formatting.None)
            .Should()
            .Be(
                """{"route":"/all","target":"pages","cursor":null,"limit":50,"mode":"replace","query":{}}"""
            );
    }

    [Fact]
    public void Every_shelf_names_an_app_component_the_server_also_sends()
    {
        List<string> names =
        [
            PluginShelves.Grid("g", []).Component,
            PluginShelves.MusicCard(CardData("c")).Component,
            PluginShelves.Row(Shelf("r")).Component,
            PluginShelves.Page("p", []).Component,
        ];

        names.Should().OnlyContain(name => NoMercy.Design.NmAppComponents.All.Contains(name));
    }

    private static PluginShelfCard CardData(string id) =>
        new()
        {
            Id = id,
            Name = id,
            Link = $"/x/{id}",
            Description = null,
        };

    private static PluginComponent Tile(string id) => PluginShelves.MusicCard(CardData(id));

    private static PluginShelf Shelf(string id, int cards = 2) =>
        new()
        {
            Id = id,
            Title = id,
            Items = [.. Enumerable.Range(0, cards).Select(index => Tile($"{id}-{index}"))],
        };

    private static PluginViewRequest Request(string route, Dictionary<string, string> query) =>
        new()
        {
            Route = route,
            Query = query,
            Caller = AnyCaller,
        };

    private static string Json(object value) => JsonConvert.SerializeObject(value, ApiSettings);

    private static JObject Parse(object value) => JObject.Parse(Json(value));

    // The settings this server writes responses with: MVC's, with the enum
    // converter the service adds.
    private static JsonSerializerSettings Settings()
    {
        JsonSerializerSettings settings = new MvcNewtonsoftJsonOptions().SerializerSettings;
        settings.Converters.Add(new StringEnumConverter());
        return settings;
    }
}
