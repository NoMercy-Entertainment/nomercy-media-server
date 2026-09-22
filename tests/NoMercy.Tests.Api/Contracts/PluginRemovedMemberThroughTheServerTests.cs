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
using Newtonsoft.Json.Linq;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.Tests.Api.Infrastructure;
using Xunit;

namespace NoMercy.Tests.Api.Contracts;

/// <summary>
/// The response a viewer gets when a plugin calls a member contract v3 took
/// away, read off a real request.
/// <para>
/// A filter tested by handing it an exception context proves the filter. It
/// cannot prove the filter is registered, that it runs on the route that
/// actually fails, or that the route spells the plugin id the way the filter
/// reads it. The first version of this filter keyed on <c>pluginId</c> and the
/// view route takes <c>id</c>, so it missed the one path Live TV fails on, and
/// a hand-built context could not tell.
/// </para>
/// </summary>
[Trait("Category", "Contracts")]
public class PluginRemovedMemberThroughTheServerTests
    : IClassFixture<PluginRemovedMemberThroughTheServerTests.Factory>
{
    private static readonly Ulid ThrowingPluginId = Ulid.Parse("01ARZ3NDEKTSV4RRFFQ69G5FAV");

    private readonly HttpClient _authed;

    public PluginRemovedMemberThroughTheServerTests(Factory factory)
    {
        _authed = factory.CreateClient().AsAuthenticated();
    }

    [Fact]
    public async Task A_view_from_a_plugin_calling_a_removed_member_answers_with_the_refusal()
    {
        HttpResponseMessage response = await _authed.GetAsync(
            $"/api/v1/plugins/{ThrowingPluginId}/view?route=/"
        );

        string raw = await response.Content.ReadAsStringAsync();
        ((int)response.StatusCode).Should().Be(501, "body was: {0}", raw);

        JObject body = JObject.Parse(raw);

        body["code"]!.Value<string>().Should().Be("PLUGIN_HOST_SERVICES_REMOVED");
        body["plugin"]!.Value<string>().Should().Be(ThrowingPluginId.ToString());
        body["what"]!
            .Value<string>()
            .Should()
            .Contain("get_Services", "the author has to know which member");
        body["why"]!.Value<string>().Should().NotBeNullOrEmpty();
        body["fix"]!.Value<string>().Should().Contain("11.0");
        body["severity"]!.Value<string>().Should().Be("blocked");
    }

    /// <summary>
    /// The same failure on an action that has no catch of its own, which is
    /// what proves the exception filter is registered in the pipeline at all.
    /// A filter handed a context by a test runs whether or not the server ever
    /// wired it up.
    /// </summary>
    [Fact]
    public async Task One_stale_plugin_does_not_take_the_addons_list_down()
    {
        HttpResponseMessage response = await _authed.GetAsync("/api/v1/plugins/ui");
        string raw = await response.Content.ReadAsStringAsync();

        ((int)response.StatusCode).Should().Be(200, "body was: {0}", raw);
        JObject body = JObject.Parse(raw);
        body["data"]!.Should().NotBeNull();
        body["data"]!.Values<JToken>().Should().BeEmpty("the one plugin here cannot answer");
    }

    [Fact]
    public async Task One_stale_plugin_does_not_take_the_browse_page_down()
    {
        HttpResponseMessage response = await _authed.GetAsync("/api/v1/plugins/ui/browse");
        string raw = await response.Content.ReadAsStringAsync();

        ((int)response.StatusCode).Should().Be(200, "body was: {0}", raw);
        JObject.Parse(raw)["data"]!.Should().NotBeNull();
    }

    /// <summary>A UI plugin whose view fails the way a v2 plugin's does.</summary>
    private sealed class ThrowingUiPlugin : IUiPlugin
    {
        public string Name => "Live TV";
        public string Description => "Calls a member contract v3 removed";
        public Ulid Id => ThrowingPluginId;
        public Version Version => new(0, 9, 1);

        public IReadOnlyList<PluginNavEntry> NavEntries =>
            throw new MissingMethodException(
                "Method not found: 'System.Collections.Generic.IReadOnlyList`1<PluginNavEntry> NoMercy.PluginSdk.Abstractions.IPluginContext.get_NavEntries()'."
            );

        public void Initialize(IPluginContext context) { }

        public void Dispose() { }

        public Task<PluginView> GetViewAsync(PluginViewRequest request, CancellationToken ct)
        {
            // What the runtime raises when a compiled v2 assembly reaches a
            // member v3 took away. It fires when the method is prepared, which
            // is why the plugin's own try/catch around the call does not catch
            // it: the failure happens at the call site, not inside the body.
            throw new MissingMethodException(
                "Method not found: 'System.IServiceProvider NoMercy.PluginSdk.Abstractions.IPluginContext.get_Services()'."
            );
        }
    }

    public class Factory : NoMercyApiFactory
    {
        protected override IPluginManager CreateTestPluginManager()
        {
            return new OneThrowingPlugin();
        }

        private sealed class OneThrowingPlugin : IPluginManager
        {
            private static readonly ThrowingUiPlugin Plugin = new();

            private static readonly PluginInfo Info = new()
            {
                Id = ThrowingPluginId,
                Name = Plugin.Name,
                Description = Plugin.Description,
                Version = Plugin.Version,
                Status = PluginStatus.Active,
                AssemblyPath = "in-memory",
                Capabilities = new() { Hooks = ["ui"] },
            };

            public IReadOnlyList<PluginInfo> GetInstalledPlugins() => [Info];

            public PluginInfo? GetPluginInfo(Ulid pluginId) =>
                pluginId == ThrowingPluginId ? Info : null;

            public IPlugin? GetPluginInstance(Ulid pluginId) =>
                pluginId == ThrowingPluginId ? Plugin : null;

            public Task InstallPluginAsync(string packageUrl, CancellationToken ct = default) =>
                Task.CompletedTask;

            public Task EnablePluginAsync(Ulid pluginId, CancellationToken ct = default) =>
                Task.CompletedTask;

            public Task DisablePluginAsync(Ulid pluginId, CancellationToken ct = default) =>
                Task.CompletedTask;

            public Task UninstallPluginAsync(Ulid pluginId, CancellationToken ct = default) =>
                Task.CompletedTask;

            public Task<IReadOnlyList<PluginLoadResult>> LoadAllAsync(
                CancellationToken ct = default
            ) => Task.FromResult<IReadOnlyList<PluginLoadResult>>([]);

            public IEnumerable<T> GetPluginsOfType<T>()
                where T : IPlugin => Plugin is T typed ? [typed] : [];
        }
    }
}
