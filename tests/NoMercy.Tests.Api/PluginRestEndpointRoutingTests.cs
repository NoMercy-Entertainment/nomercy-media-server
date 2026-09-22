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
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using NoMercy.Api.Plugins;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Mvc;
using NoMercy.Tests.Api.Infrastructure;
using Xunit;

namespace NoMercy.Tests.Api;

/// <summary>
/// A plugin controller attached the way the platform attaches one, then called
/// on the URL the web client actually posts to. The unit test over the
/// convention pins the template; this proves the version segment resolves at
/// request time, which is the half a template assertion cannot see.
/// </summary>
[Trait("Category", "Routes")]
public class PluginRestEndpointRoutingTests : IClassFixture<PluginRestEndpointRoutingTests.Factory>
{
    public static readonly Ulid PluginId = Ulid.NewUlid();

    private readonly Factory _factory;

    public PluginRestEndpointRoutingTests(Factory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Versioned_Plugin_Route_Reaches_The_Controller()
    {
        HttpClient client = _factory.CreateClientWithPluginAttached();

        HttpResponseMessage response = await client.GetAsync(
            $"/api/v1/plugins/{PluginId}/settings"
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(PluginId.ToString(), await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Unversioned_Plugin_Route_Is_Not_Served()
    {
        HttpClient client = _factory.CreateClientWithPluginAttached();

        HttpResponseMessage response = await client.GetAsync($"/api/plugins/{PluginId}/settings");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    public class Factory : NoMercyApiFactory
    {
        protected override IPluginManager CreateTestPluginManager() => new RestPluginManager();

        public HttpClient CreateClientWithPluginAttached()
        {
            HttpClient client = this.CreateClient();

            PluginApplicationPartRegistrar registrar =
                Services.GetRequiredService<PluginApplicationPartRegistrar>();
            RestPluginManager manager = (RestPluginManager)
                Services.GetRequiredService<IPluginManager>();

            registrar.Attach(manager.GetInstalledPlugins()[0], manager);

            return client;
        }
    }

    private sealed class RestPluginManager : IPluginManager
    {
        private readonly PluginInfo _info = new()
        {
            Id = PluginId,
            Name = "Routing Sample",
            Description = "Carries one REST controller.",
            Version = new(1, 0),
            Status = PluginStatus.Active,
            Capabilities = new() { Rest = true },
        };

        public IReadOnlyList<PluginInfo> GetInstalledPlugins() => [_info];

        public IPlugin? GetPluginInstance(Ulid pluginId) =>
            pluginId == PluginId ? new SamplePlugin() : null;

        public Task InstallPluginAsync(string packageUrl, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task EnablePluginAsync(Ulid pluginId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task DisablePluginAsync(Ulid pluginId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task UninstallPluginAsync(Ulid pluginId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<PluginLoadResult>> LoadAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PluginLoadResult>>([]);

        public IEnumerable<T> GetPluginsOfType<T>()
            where T : IPlugin => [];
    }

    private sealed class SamplePlugin : IPlugin
    {
        public string Name => "Routing Sample";
        public string Description => "Carries one REST controller.";
        public Ulid Id => PluginId;
        public Version Version => new(1, 0);

        public void Initialize(IPluginContext context) { }

        public void Dispose() { }
    }
}

/// <summary>
/// Lives at namespace scope, public and unnested, because MVC only discovers a
/// controller the way it would find one inside a real plugin assembly.
/// </summary>
public class SampleRoutingPluginController : PluginControllerBase
{
    [HttpGet("settings")]
    public IActionResult Settings() => Content(PluginId.ToString());
}
