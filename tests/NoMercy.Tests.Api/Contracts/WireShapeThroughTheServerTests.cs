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

using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using NoMercy.NmSystem.Dto;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.Tests.Api.Infrastructure;
using Serilog.Events;
using Xunit;

namespace NoMercy.Tests.Api.Contracts;

/// <summary>
/// The keys a client receives, taken from the running server rather than from
/// settings a test built for itself.
/// <para>
/// Settings assembled inside a test agree with the test. They cannot see a
/// resolver the application registers, or one it does not. That gap already
/// shipped a change that turned every response in the API from camel case to
/// pascal case while a hand-built check stayed green, so the settings here come
/// out of the server's own container.
/// </para>
/// </summary>
[Trait("Category", "Contracts")]
public class WireShapeThroughTheServerTests : IClassFixture<NoMercyApiFactory>
{
    private readonly NoMercyApiFactory _factory;
    private readonly HttpClient _authed;

    public WireShapeThroughTheServerTests(NoMercyApiFactory factory)
    {
        _factory = factory;
        _authed = factory.CreateClient().AsAuthenticated();
    }

    /// <summary>The settings the pipeline serializes every response with.</summary>
    private JsonSerializerSettings ServerSettings()
    {
        return _factory
            .Services.GetRequiredService<IOptions<MvcNewtonsoftJsonOptions>>()
            .Value.SerializerSettings;
    }

    private JObject SerializeAsTheServerWould(object value)
    {
        return JObject.Parse(JsonConvert.SerializeObject(value, ServerSettings()));
    }

    /// <summary>
    /// The naming strategy the pipeline holds, which is what decides every key.
    /// <para>
    /// The framework sets a <see cref="DefaultContractResolver" /> and gives it a
    /// camel case strategy. Subclassing that resolver inherits the type and not
    /// the strategy, so a subclass registered here renames every property of
    /// every response to pascal case. That shipped, and a check that asserted
    /// the resolver's type alone would have let it through.
    /// </para>
    /// </summary>
    [Fact]
    public void The_api_names_every_property_with_the_frameworks_camel_case_strategy()
    {
        IContractResolver resolver = ServerSettings().ContractResolver!;

        resolver
            .Should()
            .BeAssignableTo<DefaultContractResolver>("the naming strategy lives on that resolver");
        ((DefaultContractResolver)resolver)
            .NamingStrategy.Should()
            .BeOfType<CamelCaseNamingStrategy>(
                "without it every property on every response is pascal case"
            );
    }

    [Fact]
    public async Task A_response_body_off_the_wire_is_camel_case()
    {
        HttpResponseMessage response = await _authed.GetAsync("/api/v1/dashboard/plugins");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        JObject body = JObject.Parse(await response.Content.ReadAsStringAsync());
        List<string> keys = [.. body.Properties().Select(property => property.Name)];

        keys.Should().Contain("data", "every response carries the envelope the clients read");
        keys.Should().NotContain("Data", "the API is camel case and every client depends on it");
    }

    [Fact]
    public void A_log_line_carries_its_message_and_level_once()
    {
        LogEntry entry = new()
        {
            Type = "info",
            Color = "blue",
            ThreadId = 7,
            Time = new DateTime(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc),
            Message = "Plugin loaded: Internet Radio 2.0.0",
            LogLevel = LogEventLevel.Information,
        };

        JObject wire = SerializeAsTheServerWould(entry);
        List<string> keys = [.. wire.Properties().Select(property => property.Name)];

        wire["message"]!.Value<string>().Should().Be("Plugin loaded: Internet Radio 2.0.0");
        wire["level"]!.Value<string>().Should().Be("Information");
        keys.Should().NotContain("LogMessage", "the message is already on the line as message");
        keys.Should().NotContain("LogLevel", "the level is already on the line as level");
    }

    [Fact]
    public void A_plugin_view_carries_the_keys_the_plugin_host_reads()
    {
        PluginView view = new()
        {
            Components =
            [
                new() { Id = "station-0", Component = "NMCard" },
                new() { Id = "station-1", Component = "NMImage" },
            ],
        };

        JObject wire = SerializeAsTheServerWould(view);
        JArray components = (JArray)wire["components"]!;

        // Ids in order, not a count: two cards sharing one id still count two.
        components
            .Select(component => component["id"]!.Value<string>())
            .Should()
            .Equal("station-0", "station-1");
        components
            .Select(component => component["component"]!.Value<string>())
            .Should()
            .Equal("NMCard", "NMImage");
    }

    [Fact]
    public void A_capability_set_carries_the_keys_the_dashboard_reads()
    {
        PluginCapabilities capabilities = new()
        {
            Hooks = ["mediaSource"],
            Rest = true,
            RestAnonymous = true,
            Ws = true,
            Network = new() { Hosts = ["somafm.com"] },
        };

        JObject wire = SerializeAsTheServerWould(capabilities);

        wire["hooks"]!.Values<string>().Should().Equal("mediaSource");
        wire["rest"]!.Value<bool>().Should().BeTrue();
        wire["restAnonymous"]!.Value<bool>().Should().BeTrue();
        wire["ws"]!.Value<bool>().Should().BeTrue();
        wire["network"]!["hosts"]!.Values<string>().Should().Equal("somafm.com");
    }
}
