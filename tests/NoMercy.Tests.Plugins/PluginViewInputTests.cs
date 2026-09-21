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
using NoMercy.Plugins.Abstractions;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// A number arrives from JSON as a JsonElement. Every plugin unwrapping that
/// by hand is every plugin getting it wrong in its own way, so the request
/// answers typed values and a plugin written before forms existed keeps
/// behaving exactly as it did.
/// </summary>
public class PluginViewInputTests
{
    private static PluginViewRequest Request(
        string? action = null,
        params (string Field, object? Value)[] values
    ) =>
        new()
        {
            Route = "/",
            Action = action,
            Caller = new(
                new(Ulid.NewUlid()),
                "Someone",
                PluginRole.Member,
                PluginAccess.Owned,
                "en",
                PluginSurface.Web
            ),
            Values = values.ToDictionary(entry => entry.Field, entry => entry.Value),
        };

    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement;

    [Fact]
    public void A_view_that_was_simply_opened_carries_no_values()
    {
        PluginViewRequest request = new()
        {
            Route = "/",
            Caller = new(
                new(Ulid.NewUlid()),
                "Someone",
                PluginRole.Member,
                PluginAccess.Owned,
                "en",
                PluginSurface.Web
            ),
        };

        request.Values.Should().BeEmpty();
        request.Action.Should().BeNull();
        request.Value<string>("anything").Should().BeNull();
    }

    [Fact]
    public void A_string_comes_back_as_a_string()
    {
        Request(values: ("query", Json("\"the matrix\"")))
            .Value<string>("query")
            .Should()
            .Be("the matrix");
    }

    [Fact]
    public void A_number_comes_back_as_a_number()
    {
        Request(values: ("year", Json("1999"))).Value<int>("year").Should().Be(1999);
    }

    [Fact]
    public void A_boolean_comes_back_as_a_boolean()
    {
        Request(values: ("subtitles", Json("true"))).Value<bool>("subtitles").Should().BeTrue();
    }

    [Fact]
    public void A_list_comes_back_as_a_list()
    {
        Request(values: ("tags", Json("""["hd","dubbed"]""")))
            .Value<string[]>("tags")
            .Should()
            .Equal(["hd", "dubbed"]);
    }

    [Fact]
    public void A_value_that_is_already_the_asked_type_is_handed_back_as_it_is()
    {
        Request(values: ("query", "the matrix")).Value<string>("query").Should().Be("the matrix");
    }

    [Fact]
    public void A_value_already_of_a_shape_no_converter_handles_is_handed_back_as_it_is()
    {
        string[] tags = ["hd", "dubbed"];

        Request(values: ("tags", tags))
            .Value<string[]>("tags")
            .Should()
            .BeSameAs(tags, "nothing converts an array, so the long way round loses it");
    }

    [Fact]
    public void A_field_the_caller_left_out_is_null_rather_than_a_throw()
    {
        Request(values: ("query", Json("\"x\""))).Value<string>("year").Should().BeNull();
    }

    [Fact]
    public void A_field_the_caller_sent_empty_is_null_rather_than_a_throw()
    {
        Request(values: ("query", null)).Value<string>("query").Should().BeNull();
    }

    [Fact]
    public void A_value_of_the_wrong_shape_is_null_rather_than_a_throw()
    {
        Request(values: ("year", Json("\"not a year\"")))
            .Value<int>("year")
            .Should()
            .Be(0, "a plugin reading a form must not be crashed by what somebody typed in it");
    }

    [Fact]
    public void The_button_that_was_pressed_is_carried()
    {
        Request("search", ("query", Json("\"x\""))).Action.Should().Be("search");
    }
}
