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
using System.Text.Json.Serialization;
using FluentAssertions;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using NoMercy.Api.Plugins;
using NoMercy.Plugins.Abstractions;
using Xunit;

namespace NoMercy.Tests.Api.Contracts;

/// <summary>
/// The keys the clients read, produced by the serializer the API actually uses.
///
/// API responses go through Newtonsoft, which cannot see a System.Text.Json
/// attribute. A DTO carrying only those ships PascalCase keys no client reads,
/// and nothing fails: the page just draws nothing.
/// </summary>
[Trait("Category", "Unit")]
public class PluginWireKeyTests
{
    private static JsonSerializerSettings ApiSettings()
    {
        JsonSerializerSettings settings = new()
        {
            ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
            DateFormatHandling = DateFormatHandling.IsoDateFormat,
            DateTimeZoneHandling = DateTimeZoneHandling.Utc,
        };
        settings.Converters.Add(new StringEnumConverter());
        settings.ContractResolver = new PluginContractResolver();
        return settings;
    }

    private static string Serialize(object value)
    {
        return JsonConvert.SerializeObject(value, ApiSettings());
    }

    [Fact]
    public void A_capability_set_travels_under_the_keys_the_dashboard_reads()
    {
        PluginCapabilities capabilities = new()
        {
            Hooks = ["mediaSource"],
            Rest = true,
            RestAnonymous = true,
            Ws = true,
            Network = new() { Hosts = ["somafm.com"] },
        };

        string json = Serialize(capabilities);

        json.Should().Contain("\"hooks\"");
        json.Should().Contain("\"rest\"");
        json.Should().Contain("\"restAnonymous\"");
        json.Should().Contain("\"ws\"");
        json.Should().Contain("\"network\"");
        json.Should().Contain("\"hosts\"");
        json.Should().NotContain("\"Hooks\"");
        json.Should().NotContain("\"RestAnonymous\"");
    }

    [Fact]
    public void A_view_travels_under_the_keys_the_plugin_host_reads()
    {
        PluginView view = new()
        {
            Components =
            [
                new() { Id = "station-0", Component = "NMCard" },
                new() { Id = "station-1", Component = "NMImage" },
            ],
        };

        JObject parsed = JObject.Parse(Serialize(view));
        JArray components = (JArray)parsed["components"]!;

        // The ids and their order, not the count: two cards that collapse to
        // one id still count two, and the second station opens the first.
        components
            .Select(component => component["id"]!.Value<string>())
            .Should()
            .Equal("station-0", "station-1");
        components
            .Select(component => component["component"]!.Value<string>())
            .Should()
            .Equal("NMCard", "NMImage");
        parsed.Properties().Select(property => property.Name).Should().NotContain("Components");
    }

    /// <summary>
    /// Every contract property, not the three a test happened to name.
    /// <para>
    /// The manifest is read by System.Text.Json and the response is written by
    /// Newtonsoft, so the one thing that has to hold is that both call a
    /// property the same thing. This walks the whole assembly and says which
    /// property disagrees, rather than leaving the types nobody wrote a test
    /// for to drift quietly.
    /// </para>
    /// </summary>
    [Fact]
    public void Every_contract_property_is_written_under_the_name_it_is_read_by()
    {
        PluginContractResolver resolver = new();
        List<string> disagreements = [];
        int checkedProperties = 0;

        foreach (Type type in typeof(PluginView).Assembly.GetExportedTypes())
        {
            if (type.IsEnum || type.IsInterface || type.IsAbstract)
                continue;

            // An exception goes through ISerializable and a converter is
            // machinery, not payload. Neither is a shape a client reads.
            if (typeof(Exception).IsAssignableFrom(type))
                continue;

            if (typeof(System.Text.Json.Serialization.JsonConverter).IsAssignableFrom(type))
                continue;

            if (resolver.ResolveContract(type) is not JsonObjectContract contract)
                continue;

            foreach (JsonProperty property in contract.Properties)
            {
                if (property.UnderlyingName is null)
                    continue;

                PropertyInfo? member = type.GetProperty(property.UnderlyingName);
                if (member is null)
                    continue;

                checkedProperties++;

                string expected =
                    member.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name
                    ?? char.ToLowerInvariant(member.Name[0]) + member.Name[1..];

                if (property.PropertyName != expected)
                    disagreements.Add(
                        $"{type.Name}.{member.Name} writes {property.PropertyName}, read as {expected}"
                    );
            }
        }

        checkedProperties.Should().BeGreaterThan(50);
        disagreements.Should().BeEmpty();
    }

    [Fact]
    public void A_ui_mount_travels_under_the_keys_the_navigation_reads()
    {
        PluginUiCapability ui = new()
        {
            Mounts =
            [
                new()
                {
                    Section = "music",
                    Label = "Radio",
                    Route = "/music/plugins/radio",
                    RequestsTopLevel = true,
                },
            ],
        };

        string json = Serialize(ui);

        json.Should().Contain("\"mounts\"");
        json.Should().Contain("\"section\"");
        json.Should().Contain("\"requestsTopLevel\"");
        json.Should().NotContain("\"RequestsTopLevel\"");
    }
}
