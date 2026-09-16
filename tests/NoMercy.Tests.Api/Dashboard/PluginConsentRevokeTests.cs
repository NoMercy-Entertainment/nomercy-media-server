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
using Moq;
using NoMercy.Api.Controllers.V1.Dashboard.Plugins;
using NoMercy.Plugins;
using NoMercy.Plugins.Abstractions;
using NoMercy.Plugins.Capabilities;
using NoMercy.Storage;
using Xunit;

namespace NoMercy.Tests.Api.Dashboard;

/// <summary>
/// Withdrawing consent is the owner saying no to everything a plugin was
/// allowed. It used to walk a fixed list of three grant kinds, so a plugin kept
/// its player.source grant - permission to play whatever URL it likes through
/// the owner's speakers - after the owner had taken its consent away.
/// </summary>
public class PluginConsentRevokeTests
{
    private static readonly Ulid PluginId = Ulid.NewUlid();

    /// <summary>
    /// Records what is held rather than that a method was called: the question
    /// is whether the plugin still holds anything afterwards.
    /// </summary>
    private class RecordingGrantStore : IPluginGrantStore
    {
        private readonly List<(Ulid Plugin, string Kind, string Value)> _grants = [];

        public IReadOnlyList<string> Granted(Ulid pluginId, string kind) =>
            [
                .. _grants
                    .Where(entry =>
                        entry.Plugin == pluginId
                        && string.Equals(entry.Kind, kind, StringComparison.OrdinalIgnoreCase)
                    )
                    .Select(entry => entry.Value),
            ];

        public bool Holds(Ulid pluginId, string kind, string value) =>
            Granted(pluginId, kind).Contains(value);

        public void Grant(Ulid pluginId, string kind, string value) =>
            _grants.Add((pluginId, kind, value));

        public void Revoke(Ulid pluginId, string kind, string value) =>
            _grants.RemoveAll(entry =>
                entry.Plugin == pluginId
                && string.Equals(entry.Kind, kind, StringComparison.OrdinalIgnoreCase)
                && string.Equals(entry.Value, value, StringComparison.OrdinalIgnoreCase)
            );

        public void RevokeAll(Ulid pluginId) =>
            _grants.RemoveAll(entry => entry.Plugin == pluginId);

        public void Request(Ulid pluginId, string kind, string value, string reason) { }

        public IReadOnlyList<PluginGrantRequest> PendingRequests() => [];

        public void ClearRequest(Ulid pluginId, string kind, string value) { }
    }

    private static PluginController BuildController(IPluginGrantStore grantStore) =>
        new(
            Mock.Of<IPluginManager>(),
            Mock.Of<IPluginConsentService>(),
            grantStore,
            Mock.Of<IPluginRestartAdvisor>(),
            Mock.Of<IStorageDriver>()
        )
        {
            ControllerContext = new() { HttpContext = new DefaultHttpContext() },
        };

    [Fact]
    public async Task Revoking_consent_leaves_the_plugin_holding_nothing()
    {
        RecordingGrantStore grantStore = new();
        grantStore.Grant(PluginId, PluginGrantKind.NetworkHost, "tracker.example");
        grantStore.Grant(PluginId, PluginGrantKind.LibraryWrite, "library-1");
        grantStore.Grant(PluginId, PluginGrantKind.PlayerSource, "ice1.somafm.com");
        grantStore.Grant(
            PluginId,
            PluginGrantKind.ForCapability(PluginCapability.Player),
            PluginGrant.Everything
        );

        await BuildController(grantStore).RevokeConsent(PluginId);

        grantStore.Granted(PluginId, PluginGrantKind.NetworkHost).Should().BeEmpty();
        grantStore.Granted(PluginId, PluginGrantKind.LibraryWrite).Should().BeEmpty();
        grantStore.Granted(PluginId, PluginGrantKind.PlayerSource).Should().BeEmpty();
        grantStore
            .Granted(PluginId, PluginGrantKind.ForCapability(PluginCapability.Player))
            .Should()
            .BeEmpty();
    }
}
