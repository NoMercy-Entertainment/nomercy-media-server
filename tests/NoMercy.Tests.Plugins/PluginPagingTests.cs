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

using System.Text.Json;
using FluentAssertions;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Ipc;
using Xunit;

namespace NoMercy.Tests.Plugins;

[Trait("Category", "Unit")]
public class PluginPagingTests
{
    private static readonly PluginCaller AnyCaller = new(
        UserId.Empty,
        "Stoney",
        PluginRole.Owner,
        PluginAccess.Owned,
        "en",
        PluginSurface.Web
    );

    [Fact]
    public void A_request_that_says_nothing_about_paging_asks_for_the_first_page()
    {
        PluginViewRequest request = new() { Route = "/", Caller = AnyCaller };

        request.Page.Should().Be(PluginPageRequest.First);
        request.Page.Cursor.Should().BeNull();
        request.Page.Limit.Should().Be(PluginPaging.DefaultPageSize);
        request.Page.IsAppend.Should().BeFalse();
        request.Page.Offset.Should().Be(0);
        request.Features.Names.Should().BeEmpty();
    }

    [Fact]
    public void An_append_request_carries_its_cursor_limit_and_shelf()
    {
        PluginPageRequest page = PluginPageRequest.From(
            new Dictionary<string, string>
            {
                ["cursor"] = "72",
                ["limit"] = "24",
                ["append"] = "all-grid",
            }
        );

        page.Cursor.Should().Be("72");
        page.Offset.Should().Be(72);
        page.Limit.Should().Be(24);
        page.Append.Should().Be("all-grid");
        page.IsAppend.Should().BeTrue();
    }

    [Theory]
    [InlineData("600", PluginPaging.MaximumPageSize)]
    [InlineData("500", 500)]
    [InlineData("99999999999999999999", PluginPaging.MaximumPageSize)]
    [InlineData("0", PluginPaging.DefaultPageSize)]
    [InlineData("-5", PluginPaging.DefaultPageSize)]
    [InlineData("ten", PluginPaging.DefaultPageSize)]
    [InlineData("", PluginPaging.DefaultPageSize)]
    [InlineData("1", 1)]
    public void The_limit_is_held_between_one_and_the_cap(string raw, int expected)
    {
        PluginPageRequest
            .From(new Dictionary<string, string> { ["limit"] = raw })
            .Limit.Should()
            .Be(expected);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("-3")]
    [InlineData("1.5")]
    public void A_cursor_that_is_not_a_whole_number_is_offset_zero_and_kept_as_written(
        string cursor
    )
    {
        PluginPageRequest page = PluginPageRequest.From(
            new Dictionary<string, string> { ["cursor"] = cursor }
        );

        page.Offset.Should().Be(0);
        page.Cursor.Should().Be(cursor);
    }

    [Fact]
    public void Features_are_read_from_one_comma_separated_value()
    {
        PluginClientFeatures features = PluginClientFeatures.From(
            new Dictionary<string, string> { ["features"] = " paging , ,future,paging" }
        );

        features.Supports(PluginClientFeatures.Paging).Should().BeTrue();
        features.Supports("future").Should().BeTrue();
        features.Supports("Paging").Should().BeFalse();
        features.Names.Should().HaveCount(2);
    }

    // rows is separate from paging: a client can page before it draws the
    // app's home row and NMLolomo, and a plugin must not send those to it.
    [Fact]
    public void Rows_is_its_own_feature_word()
    {
        PluginClientFeatures pagingOnly = PluginClientFeatures.From(
            new Dictionary<string, string> { ["features"] = "paging" }
        );
        PluginClientFeatures both = PluginClientFeatures.From(
            new Dictionary<string, string> { ["features"] = "paging,rows" }
        );

        PluginClientFeatures.Rows.Should().Be("rows");
        pagingOnly.Supports(PluginClientFeatures.Rows).Should().BeFalse();
        both.Supports(PluginClientFeatures.Rows).Should().BeTrue();
        both.Supports(PluginClientFeatures.Paging).Should().BeTrue();
    }

    [Fact]
    public void A_load_page_action_has_the_shape_the_client_runs()
    {
        PluginActionIntent action = PluginActionIntent.LoadPage(
            "/stations",
            "all-grid",
            "36",
            limit: 9000,
            query: new Dictionary<string, string>
            {
                ["country"] = "NL",
                ["cursor"] = "0",
                ["limit"] = "36",
                ["append"] = "x",
                ["features"] = "paging",
                ["surface"] = "tv",
                ["input"] = "dpad",
                ["route"] = "/",
            }
        );

        action.Type.Should().Be("loadPage");
        action.Payload["route"].Should().Be("/stations");
        action.Payload["target"].Should().Be("all-grid");
        action.Payload["cursor"].Should().Be("36");
        action.Payload["limit"].Should().Be(PluginPaging.MaximumPageSize);
        action.Payload["mode"].Should().Be("append");
        action
            .Payload["query"]
            .Should()
            .BeEquivalentTo(new Dictionary<string, string> { ["country"] = "NL" });
        PluginActionType.All.Should().Contain(action.Type);
    }

    [Theory]
    [InlineData("replace", "replace")]
    [InlineData("append", "append")]
    [InlineData("sideways", "append")]
    public void An_unknown_mode_is_an_append(string mode, string expected)
    {
        PluginActionIntent
            .LoadPage("/", "t", null, mode: mode)
            .Payload["mode"]
            .Should()
            .Be(expected);
    }

    [Fact]
    public void The_paging_members_do_not_travel_over_the_plugin_wire()
    {
        PluginViewRequest request = new()
        {
            Route = "/",
            Query = new() { ["cursor"] = "5", ["features"] = "paging" },
            Caller = AnyCaller,
        };

        string json = JsonSerializer.Serialize(request, PluginWireJson.Options);

        json.Should().NotContain("\"page\"").And.NotContain("\"features\":{");

        PluginViewRequest back = JsonSerializer.Deserialize<PluginViewRequest>(
            json,
            PluginWireJson.Options
        )!;
        back.Page.Cursor.Should().Be("5");
        back.Features.Supports(PluginClientFeatures.Paging).Should().BeTrue();
    }

    [Fact]
    public void The_reserved_query_keys_are_the_ones_clients_and_the_host_add()
    {
        PluginPaging
            .ReservedQueryKeys.Should()
            .BeEquivalentTo(["route", "surface", "input", "features", "cursor", "limit", "append"]);
    }
}
