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

using System.Diagnostics;
using System.Security.Claims;
using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Hooks;
using Xunit;

namespace NoMercy.Tests.Plugins;

public class PluginClaimsAugmentorTests
{
    [Fact]
    public async Task CollectsAdditionalClaims_FromDeclaredAuthPlugin()
    {
        FakeAuthPlugin plugin = new("plan", "pro");
        FakePluginManager manager = FakePluginManager.WithAuth(plugin);
        PluginClaimsAugmentor augmentor = new(manager, NullLogger<PluginClaimsAugmentor>.Instance);

        IReadOnlyList<Claim> claims = await augmentor.CollectAdditionalClaimsAsync("tok", default);

        Assert.Contains(claims, c => c is { Type: "plan", Value: "pro" });
    }

    [Fact]
    public async Task UndeclaredAuthPlugin_IsIgnored()
    {
        FakeAuthPlugin plugin = new("plan", "pro");
        FakePluginManager manager = FakePluginManager.WithBaselineOnly(plugin);
        PluginClaimsAugmentor augmentor = new(manager, NullLogger<PluginClaimsAugmentor>.Instance);

        IReadOnlyList<Claim> claims = await augmentor.CollectAdditionalClaimsAsync("tok", default);

        Assert.Empty(claims);
    }

    [Fact]
    public async Task ThrowingAuthPlugin_IsIgnored_AuthIsNeverWeakened()
    {
        ThrowingAuthPlugin plugin = new();
        FakePluginManager manager = FakePluginManager.WithAuth(plugin);
        PluginClaimsAugmentor augmentor = new(manager, NullLogger<PluginClaimsAugmentor>.Instance);

        IReadOnlyList<Claim> claims = await augmentor.CollectAdditionalClaimsAsync("tok", default);

        Assert.Empty(claims);
    }

    [Fact]
    public async Task UnauthenticatedPluginResult_ContributesNoClaims()
    {
        FakeAuthPlugin plugin = new("plan", "pro", isAuthenticated: false);
        FakePluginManager manager = FakePluginManager.WithAuth(plugin);
        PluginClaimsAugmentor augmentor = new(manager, NullLogger<PluginClaimsAugmentor>.Instance);

        IReadOnlyList<Claim> claims = await augmentor.CollectAdditionalClaimsAsync("tok", default);

        Assert.Empty(claims);
    }

    [Fact]
    public async Task HangingAuthPlugin_IsSkippedWithinTimeout_AuthNotBlocked()
    {
        HangingAuthPlugin plugin = new();
        FakePluginManager manager = FakePluginManager.WithAuth(plugin);
        PluginClaimsAugmentor augmentor = new(manager, NullLogger<PluginClaimsAugmentor>.Instance)
        {
            PerPluginTimeout = TimeSpan.FromMilliseconds(50),
        };

        Stopwatch stopwatch = Stopwatch.StartNew();
        IReadOnlyList<Claim> claims = await augmentor.CollectAdditionalClaimsAsync("tok", default);
        stopwatch.Stop();

        Assert.Empty(claims);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"expected the hang to be cut short by PerPluginTimeout, took {stopwatch.Elapsed}"
        );
    }

    [Fact]
    public async Task AuthPluginMissingFromInstalledList_IsIgnored()
    {
        // GetPluginsOfType and GetInstalledPlugins are two independent reads of
        // the registry; if a plugin instance is returned by the former but has
        // no matching entry in the latter, `installed.FirstOrDefault(...)`
        // returns null and the null-conditional `?.Capabilities` must fall back
        // to null capabilities (denies by default) rather than throw.
        FakeAuthPlugin plugin = new("plan", "pro");
        FakePluginManager manager = FakePluginManager.WithAuthPluginNotInInstalledList(plugin);
        PluginClaimsAugmentor augmentor = new(manager, NullLogger<PluginClaimsAugmentor>.Instance);

        IReadOnlyList<Claim> claims = await augmentor.CollectAdditionalClaimsAsync("tok", default);

        Assert.Empty(claims);
    }

    [Fact]
    public async Task ReservedClaimTypes_AreStrippedFromPluginOutput()
    {
        MultiClaimAuthPlugin plugin = new();
        FakePluginManager manager = FakePluginManager.WithAuth(plugin);
        PluginClaimsAugmentor augmentor = new(manager, NullLogger<PluginClaimsAugmentor>.Instance);

        IReadOnlyList<Claim> claims = await augmentor.CollectAdditionalClaimsAsync("tok", default);

        Assert.Single(claims);
        Assert.Contains(claims, c => c is { Type: "plan", Value: "pro" });
        Assert.DoesNotContain(claims, c => c.Type == ClaimTypes.Role);
        Assert.DoesNotContain(claims, c => c.Type == "sub");
    }

    private sealed class HangingAuthPlugin : IAuthPlugin
    {
        public string Name => "hanging-auth";
        public string Description => "d";
        public Ulid Id { get; } = Ulid.NewUlid();
        public Version Version { get; } = new(1, 0);

        public void Initialize(IPluginContext context) { }

        public void Dispose() { }

        public async Task<AuthResult> AuthenticateAsync(
            string token,
            CancellationToken ct = default
        )
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return new AuthResult
            {
                IsAuthenticated = true,
                Claims = new() { ["never"] = "reached" },
            };
        }
    }

    private sealed class MultiClaimAuthPlugin : IAuthPlugin
    {
        public string Name => "multi-claim-auth";
        public string Description => "d";
        public Ulid Id { get; } = Ulid.NewUlid();
        public Version Version { get; } = new(1, 0);

        public void Initialize(IPluginContext context) { }

        public void Dispose() { }

        public Task<AuthResult> AuthenticateAsync(string token, CancellationToken ct = default) =>
            Task.FromResult(
                new AuthResult
                {
                    IsAuthenticated = true,
                    Claims = new()
                    {
                        [ClaimTypes.Role] = "super-admin",
                        ["sub"] = "attacker",
                        ["plan"] = "pro",
                    },
                }
            );
    }

    private sealed class FakeAuthPlugin(
        string claimType,
        string claimValue,
        bool isAuthenticated = true
    ) : IAuthPlugin
    {
        public string Name => "fake-auth";
        public string Description => "d";
        public Ulid Id { get; } = Ulid.NewUlid();
        public Version Version { get; } = new(1, 0);

        public void Initialize(IPluginContext context) { }

        public void Dispose() { }

        public Task<AuthResult> AuthenticateAsync(string token, CancellationToken ct = default) =>
            Task.FromResult(
                new AuthResult
                {
                    IsAuthenticated = isAuthenticated,
                    Claims = new() { [claimType] = claimValue },
                }
            );
    }

    private sealed class ThrowingAuthPlugin : IAuthPlugin
    {
        public string Name => "throwing-auth";
        public string Description => "d";
        public Ulid Id { get; } = Ulid.NewUlid();
        public Version Version { get; } = new(1, 0);

        public void Initialize(IPluginContext context) { }

        public void Dispose() { }

        public Task<AuthResult> AuthenticateAsync(string token, CancellationToken ct = default) =>
            throw new InvalidOperationException("plugin blew up");
    }

    /// <summary>
    /// Minimal <see cref="IPluginManager"/> test double whose <see cref="GetInstalledPlugins"/>
    /// mirrors the real registry shape (<see cref="PluginInfo.Capabilities"/> keyed by plugin
    /// id) so the dispatcher's capability lookup is exercised exactly as in production.
    /// </summary>
    private sealed class FakePluginManager : IPluginManager
    {
        private readonly List<IAuthPlugin> _plugins = [];
        private readonly Dictionary<Ulid, PluginCapabilities?> _capabilities = [];

        public static FakePluginManager WithAuth(IAuthPlugin plugin)
        {
            FakePluginManager manager = new();
            manager._plugins.Add(plugin);
            manager._capabilities[plugin.Id] = new() { Hooks = [PluginHookCapability.Auth] };
            return manager;
        }

        public static FakePluginManager WithBaselineOnly(IAuthPlugin plugin)
        {
            FakePluginManager manager = new();
            manager._plugins.Add(plugin);
            manager._capabilities[plugin.Id] = null;
            return manager;
        }

        // Deliberately adds the plugin to _plugins (so GetPluginsOfType returns it)
        // WITHOUT a corresponding _capabilities entry, so GetInstalledPlugins omits
        // it entirely — reproducing a registry read that is out of sync with the
        // live plugin list.
        public static FakePluginManager WithAuthPluginNotInInstalledList(IAuthPlugin plugin)
        {
            FakePluginManager manager = new();
            manager._plugins.Add(plugin);
            return manager;
        }

        public IReadOnlyList<PluginInfo> GetInstalledPlugins() =>
            _plugins
                .Where(plugin => _capabilities.ContainsKey(plugin.Id))
                .Select(plugin => new PluginInfo
                {
                    Id = plugin.Id,
                    Name = plugin.Name,
                    Description = plugin.Description,
                    Version = plugin.Version,
                    Status = PluginStatus.Active,
                    Capabilities = _capabilities[plugin.Id],
                })
                .ToList();

        public IEnumerable<T> GetPluginsOfType<T>()
            where T : IPlugin => _plugins.OfType<T>();

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
    }
}
