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
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
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
        PluginView view = new() { Components = [new() { Id = "station-0", Component = "NMCard" }] };

        string json = Serialize(view);

        json.Should().Contain("\"components\"");
        json.Should().Contain("\"id\"");
        json.Should().Contain("\"component\"");
        json.Should().NotContain("\"Components\"");
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
