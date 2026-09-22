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
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using NoMercy.Api.Controllers.V1.Dashboard.Plugins;
using NoMercy.Api.DTOs.Common;
using NoMercy.PluginSdk;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Capabilities;
using NoMercy.PluginSdk.Sideload;
using NoMercy.Storage;
using Xunit;

namespace NoMercy.Tests.Api.Dashboard;

/// <summary>
/// A plugin whose manifest asks for more than its recorded consent covers is
/// waiting on the owner, not broken. The dashboard read that state from "is
/// there any consent at all", which is true for a record that covers a smaller
/// request, so the plugin showed as plain Disabled with no way out.
/// </summary>
public class PluginAwaitingConsentTests
{
    private static readonly Ulid PluginId = Ulid.NewUlid();

    private static PluginCapabilities Consented() => new() { Rest = true };

    private static PluginCapabilities Widened() => new() { Rest = true, Ws = true };

    private class SinglePluginManager(PluginCapabilities capabilities) : IPluginManager
    {
        public IReadOnlyList<PluginInfo> GetInstalledPlugins() =>
            [
                new()
                {
                    Id = PluginId,
                    Name = "Internet Radio",
                    Description = "d",
                    Version = new(1, 5, 0),
                    Status = PluginStatus.Disabled,
                    Capabilities = capabilities,
                },
            ];

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

    private static PluginInfoDto Describe(
        PluginCapabilities installed,
        IPluginConsentService consentService
    )
    {
        PluginController controller = new(
            new SinglePluginManager(installed),
            consentService,
            Mock.Of<IPluginGrantStore>(),
            Mock.Of<IPluginRestartAdvisor>(),
            Mock.Of<IStorageDriver>(),
            Mock.Of<IPluginDeveloperModeSource>()
        )
        {
            ControllerContext = new() { HttpContext = new DefaultHttpContext() },
        };

        OkObjectResult result = (OkObjectResult)controller.Show(PluginId);
        DataResponseDto<PluginInfoDto> payload = (DataResponseDto<PluginInfoDto>)result.Value!;

        return payload.Data!;
    }

    private static bool AwaitingConsentOf(
        PluginCapabilities installed,
        IPluginConsentService consentService
    ) => Describe(installed, consentService).AwaitingConsent;

    private static IPluginConsentService ConsentFor(PluginCapabilities? capabilities)
    {
        InMemoryConsentStore store = new();

        if (capabilities is not null)
            store.Add(PluginId, capabilities, new System.Version(1, 4, 0));

        return new PluginConsentService(store);
    }

    [Fact]
    public void A_manifest_that_widened_past_its_consent_is_awaiting_the_owner()
    {
        AwaitingConsentOf(Widened(), ConsentFor(Consented()))
            .Should()
            .BeTrue("consent exists, and it does not cover what this manifest now asks for");
    }

    [Fact]
    public void A_manifest_its_consent_still_covers_is_not_awaiting_anyone()
    {
        AwaitingConsentOf(Consented(), ConsentFor(Consented())).Should().BeFalse();
    }

    [Fact]
    public void An_elevated_plugin_with_no_consent_at_all_is_awaiting_the_owner()
    {
        AwaitingConsentOf(Consented(), ConsentFor(null)).Should().BeTrue();
    }

    [Fact]
    public void A_widened_plugin_carries_both_the_old_set_and_the_new_one()
    {
        // The owner cannot answer "this update wants more" without being told
        // what more is. Both sets on one payload is what lets a client name it.
        PluginInfoDto described = Describe(Widened(), ConsentFor(Consented()));

        described.AwaitingConsent.Should().BeTrue();
        described.Capabilities!.Ws.Should().BeTrue("the manifest now asks for ws");
        described.ConsentedCapabilities.Should().NotBeNull();
        described.ConsentedCapabilities!.Rest.Should().BeTrue();
        described.ConsentedCapabilities.Ws.Should().BeFalse("the owner never approved ws");
    }

    [Fact]
    public void A_plugin_that_was_never_approved_carries_no_consented_set()
    {
        Describe(Consented(), ConsentFor(null)).ConsentedCapabilities.Should().BeNull();
    }

    private sealed class InMemoryConsentStore : IPluginConsentStore
    {
        private readonly Dictionary<Ulid, PluginConsentGrant> _granted = [];

        public bool Contains(Ulid pluginId) => _granted.ContainsKey(pluginId);

        public PluginConsentGrant? Get(Ulid pluginId) =>
            _granted.TryGetValue(pluginId, out PluginConsentGrant? grant) ? grant : null;

        public void Add(
            Ulid pluginId,
            PluginCapabilities? capabilities,
            System.Version manifestVersion
        )
        {
            // Carries the per-capability answers over, the way the real store
            // does. Dropping them here would let a test pass on a store that
            // silently re-approves everything the owner said no to.
            _granted.TryGetValue(pluginId, out PluginConsentGrant? existing);

            _granted[pluginId] = new PluginConsentGrant
            {
                Capabilities = capabilities,
                ManifestVersion = manifestVersion.ToString(),
                ApprovedCapabilities =
                    existing?.ApprovedCapabilities ?? new(StringComparer.Ordinal),
            };
        }

        public void Save(Ulid pluginId, PluginConsentGrant grant) => _granted[pluginId] = grant;

        public void Remove(Ulid pluginId) => _granted.Remove(pluginId);
    }
}
