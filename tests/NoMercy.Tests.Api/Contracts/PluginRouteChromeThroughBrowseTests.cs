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

using Newtonsoft.Json.Linq;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.Tests.Api.Infrastructure;
using Xunit;

namespace NoMercy.Tests.Api.Contracts;

/// <summary>
/// What a client reads off the browse response for a route's chrome, taken from
/// a real request so the field name on the wire is the one the clients parse.
/// </summary>
[Trait("Category", "Contracts")]
public class PluginRouteChromeThroughBrowseTests
    : IClassFixture<PluginRouteChromeThroughBrowseTests.Factory>
{
    private static readonly Ulid ChromePluginId = Ulid.Parse("01ARZ3NDEKTSV4RRFFQ69G5FB0");

    private readonly HttpClient _authed;

    public PluginRouteChromeThroughBrowseTests(Factory factory)
    {
        _authed = factory.CreateClient().AsAuthenticated();
    }

    private async Task<JToken> PageNamed(string name)
    {
        HttpResponseMessage response = await _authed.GetAsync("/api/v1/plugins/ui/browse");
        string raw = await response.Content.ReadAsStringAsync();

        ((int)response.StatusCode).Should().Be(200, "body was: {0}", raw);

        JToken? page = JObject.Parse(raw)["data"]!
            .SelectMany(group => group["entries"]!)
            .SelectMany(entry => entry["pages"]!)
            .FirstOrDefault(candidate => candidate["name"]!.Value<string>() == name);

        page.Should().NotBeNull("body was: {0}", raw);

        return page!;
    }

    [Fact]
    public async Task A_page_that_owns_the_screen_reports_chrome_none()
    {
        JToken page = await PageNamed("player");

        page["chrome"]!.Value<string>().Should().Be("none");
    }

    [Fact]
    public async Task A_page_that_says_nothing_reports_chrome_app()
    {
        JToken page = await PageNamed("info");

        page["chrome"]!.Value<string>().Should().Be("app");
    }

    private sealed class ChromePlugin : IUiPlugin
    {
        public string Name => "Chrome sample";
        public string Description => "Declares one page with and one without the navbar";
        public Ulid Id => ChromePluginId;
        public Version Version => new(1, 0, 0);

        public IReadOnlyList<PluginNavEntry> NavEntries =>
            [
                new PluginNavEntry
                {
                    Section = PluginUiSection.Dashboard,
                    Label = "chrome.title",
                    Route = "/info",
                },
            ];

        public PluginRouteTable Routes { get; } =
            new(
                new PluginRoute { Path = "/info", Name = "info" },
                new PluginRoute
                {
                    Path = "/player",
                    Name = "player",
                    Chrome = PluginChrome.None,
                }
            );

        public void Initialize(IPluginContext context) { }

        public void Dispose() { }

        public Task<PluginView> GetViewAsync(PluginViewRequest request, CancellationToken ct) =>
            Task.FromResult(new PluginView());
    }

    public class Factory : NoMercyApiFactory
    {
        protected override IPluginManager CreateTestPluginManager()
        {
            return new OneChromePlugin();
        }

        private sealed class OneChromePlugin : IPluginManager
        {
            private static readonly ChromePlugin Plugin = new();

            private static readonly PluginInfo Info = new()
            {
                Id = ChromePluginId,
                Name = Plugin.Name,
                Description = Plugin.Description,
                Version = Plugin.Version,
                Status = PluginStatus.Active,
                AssemblyPath = "in-memory",
                Capabilities = new() { Hooks = ["ui"] },
            };

            public IReadOnlyList<PluginInfo> GetInstalledPlugins() => [Info];

            public PluginInfo? GetPluginInfo(Ulid pluginId) =>
                pluginId == ChromePluginId ? Info : null;

            public IPlugin? GetPluginInstance(Ulid pluginId) =>
                pluginId == ChromePluginId ? Plugin : null;

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
