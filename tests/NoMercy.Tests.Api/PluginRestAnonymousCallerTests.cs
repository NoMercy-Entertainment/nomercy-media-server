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
using Microsoft.Extensions.DependencyInjection;
using NoMercy.Api.Plugins;
using NoMercy.Plugins.Abstractions;
using NoMercy.Tests.Api.Infrastructure;
using Xunit;

namespace NoMercy.Tests.Api;

/// <summary>
/// The convention test pins the metadata; these call a plugin route with no
/// token through the real pipeline, which is the half a metadata assertion
/// cannot see. Both answers matter: a plugin route is shut by default, and the
/// manifest is what opens one.
/// </summary>
[Trait("Category", "Authorization")]
public class PluginRestAnonymousCallerTests
{
    public static readonly Ulid ClosedPluginId = Ulid.NewUlid();
    public static readonly Ulid OpenPluginId = Ulid.NewUlid();

    [Fact]
    public async Task A_caller_with_no_token_does_not_reach_a_plugin_route()
    {
        await using ClosedFactory factory = new();
        HttpClient client = factory.CreateClientWithPluginAttached();

        HttpResponseMessage response = await client.GetAsync(
            $"/api/v1/plugins/{ClosedPluginId}/settings"
        );

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_manifest_that_declares_an_open_rest_surface_answers_without_a_token()
    {
        await using OpenFactory factory = new();
        HttpClient client = factory.CreateClientWithPluginAttached();

        HttpResponseMessage response = await client.GetAsync(
            $"/api/v1/plugins/{OpenPluginId}/settings"
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    public class ClosedFactory : PluginFactory
    {
        protected override PluginInfo Plugin { get; } =
            Describe(ClosedPluginId, new() { Rest = true });
    }

    public class OpenFactory : PluginFactory
    {
        protected override PluginInfo Plugin { get; } =
            Describe(OpenPluginId, new() { Rest = true, RestAnonymous = true });
    }

    public abstract class PluginFactory : AnonymousNoMercyApiFactory
    {
        protected abstract PluginInfo Plugin { get; }

        protected static PluginInfo Describe(Ulid id, PluginCapabilities capabilities) =>
            new()
            {
                Id = id,
                Name = "Routing Sample",
                Description = "Carries one REST controller.",
                Version = new(1, 0),
                Status = PluginStatus.Active,
                Capabilities = capabilities,
            };

        protected override IPluginManager CreateTestPluginManager() =>
            new RestPluginManager(Plugin);

        public HttpClient CreateClientWithPluginAttached()
        {
            HttpClient client = CreateClient();

            PluginApplicationPartRegistrar registrar =
                Services.GetRequiredService<PluginApplicationPartRegistrar>();
            IPluginManager manager = Services.GetRequiredService<IPluginManager>();

            registrar.Attach(manager.GetInstalledPlugins()[0], manager);

            return client;
        }
    }

    private sealed class RestPluginManager(PluginInfo info) : IPluginManager
    {
        public IReadOnlyList<PluginInfo> GetInstalledPlugins() => [info];

        public IPlugin? GetPluginInstance(Ulid pluginId) =>
            pluginId == info.Id ? new SamplePlugin(info.Id) : null;

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

    private sealed class SamplePlugin(Ulid id) : IPlugin
    {
        public string Name => "Routing Sample";
        public string Description => "Carries one REST controller.";
        public Ulid Id => id;
        public Version Version => new(1, 0);

        public void Initialize(IPluginContext context) { }

        public void Dispose() { }
    }
}
