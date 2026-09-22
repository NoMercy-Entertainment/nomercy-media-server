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

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.Events;
using NoMercy.PluginSdk;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Network;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// Proves the plugin's declared network capabilities actually reach
/// <see cref="PluginContext.HttpClient"/> instead of being dropped at
/// construction. <see cref="PluginContext"/> wires a real
/// <see cref="SocketsHttpHandler"/> with no injectable seam, so both
/// assertions exercise the production object end to end without touching
/// the network: a disallowed host is rejected by our own guard before any
/// transport I/O runs, and an allowed host is proven to have cleared that
/// same guard by observing the *different* failure (cancellation) that only
/// the real transport can produce.
/// </summary>
public class PluginContextNetworkWiringTests
{
    private static PluginContext BuildContext(string tempDir, PluginCapabilities? capabilities)
    {
        InMemoryEventBus eventBus = new();
        MinimalServiceProvider services = new();
        ILogger logger = NullLogger.Instance;

        return TestPluginPlatform.Context(
            eventBus,
            tempDir,
            TestStorageHelper.CreateStorage(tempDir),
            services: services,
            capabilities: capabilities
        );
    }

    [Fact]
    public async Task DeclaredNetworkCapability_DeniesHostOutsideAllowlist()
    {
        string tempDir = Directory.CreateTempSubdirectory("plugin-ctx-net-").FullName;
        PluginCapabilities capabilities = new() { Network = new() { Hosts = ["*.somafm.com"] } };
        PluginContext context = BuildContext(tempDir, capabilities);

        await Assert.ThrowsAsync<PluginNetworkDeniedException>(() =>
            context.HttpClient.GetAsync("https://evil.example.com/x")
        );
    }

    [Fact]
    public async Task DeclaredNetworkCapability_ClearsAllowlistGuard_ForMatchingHost()
    {
        string tempDir = Directory.CreateTempSubdirectory("plugin-ctx-net-").FullName;
        PluginCapabilities capabilities = new() { Network = new() { Hosts = ["*.somafm.com"] } };
        PluginContext context = BuildContext(tempDir, capabilities);

        using CancellationTokenSource alreadyCanceled = new();
        alreadyCanceled.Cancel();

        // An allowed host must not throw PluginNetworkDeniedException — it must clear our
        // guard and reach the real SocketsHttpHandler, which then fails on the pre-canceled
        // token instead. That different failure is the proof the host pattern was honored.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            context.HttpClient.GetAsync("https://ice1.somafm.com/x", alreadyCanceled.Token)
        );
    }

    [Fact]
    public async Task NullCapabilities_DenyAllHosts()
    {
        string tempDir = Directory.CreateTempSubdirectory("plugin-ctx-net-").FullName;
        PluginContext context = BuildContext(tempDir, null);

        await Assert.ThrowsAsync<PluginNetworkDeniedException>(() =>
            context.HttpClient.GetAsync("https://ice1.somafm.com/x")
        );
    }

    [Fact]
    public async Task NonNullCapabilities_WithoutNetworkSection_DenyAllHosts()
    {
        // Distinct from NullCapabilities_DenyAllHosts above: capabilities is a
        // real, non-null object here, but its Network property was never set —
        // PluginHttpClientFactory.Create's `capabilities?.Network?.Hosts` chain
        // must fall back to an empty allowlist for this combination too, not
        // just when capabilities itself is null.
        string tempDir = Directory.CreateTempSubdirectory("plugin-ctx-net-").FullName;
        PluginContext context = BuildContext(tempDir, new PluginCapabilities());

        await Assert.ThrowsAsync<PluginNetworkDeniedException>(() =>
            context.HttpClient.GetAsync("https://ice1.somafm.com/x")
        );
    }

    private sealed class MinimalServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
