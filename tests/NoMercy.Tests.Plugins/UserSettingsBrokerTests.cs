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
using NoMercy.Plugins.Abstractions;
using NoMercy.Plugins.Capabilities;
using NoMercy.Plugins.Ipc;
using NoMercy.Plugins.OutOfProcess;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// Settings, and what one person has watched, saved and chosen.
/// <para>
/// Each of the four user answers is its own capability rather than one for the
/// lot: a plugin that needs a display name has no business reading a watch
/// history, and the owner's permissions page says so line by line.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public class UserSettingsBrokerTests
{
    private static readonly Ulid PluginId = Ulid.NewUlid();

    [Fact]
    public async Task ASettingCrossesBackAsTheJsonItWasStoredAs()
    {
        PluginCallResponse response = await Ask(
            new FakeCapabilities(null),
            "settings",
            nameof(IPluginSettings.Get),
            """{"key":"station"}"""
        );

        response.Ok.Should().BeTrue();
        response.PayloadJson.Should().Be("\"BBC Radio 4\"");
    }

    [Fact]
    public async Task ASettingsCallWithoutTheCapability_IsRefused()
    {
        PluginCallResponse response = await Ask(
            Refusing(),
            "settings",
            nameof(IPluginSettings.Get),
            """{"key":"station"}"""
        );

        response.Ok.Should().BeFalse();
        response.Refusal!.Code.Should().Be(PluginRefusalCodes.CapabilityNotDeclared);
    }

    /// <summary>
    /// The four answers are four capabilities. One gate for all of them would
    /// mean a plugin granted a display name could read the household's whole
    /// watch history.
    /// </summary>
    [Theory]
    [InlineData("IdentityAsync", PluginCapabilityNames.UserIdentity)]
    [InlineData("WatchAsync", PluginCapabilityNames.UserWatch)]
    [InlineData("PlaylistsAsync", PluginCapabilityNames.UserPlaylists)]
    [InlineData("PreferencesAsync", PluginCapabilityNames.UserPreferences)]
    public async Task EachUserAnswerIsGatedByItsOwnCapability(string member, string capability)
    {
        ScopeRecordingCapabilities capabilities = new();

        await Ask(capabilities, "user", member);

        capabilities.Capability.Should().Be(capability);
    }

    [Fact]
    public async Task AUserCallWithoutItsCapability_IsRefusedAndNothingIsRead()
    {
        RecordingUserData data = new();

        PluginCallResponse response = await Ask(
            Refusing(),
            "user",
            nameof(IPluginUserData.WatchAsync),
            "{}",
            data
        );

        response.Ok.Should().BeFalse();
        data.Reads.Should().Be(0);
    }

    [Fact]
    public async Task AUserMemberThatDoesNotCrossYet_SaysSoByName()
    {
        PluginCallResponse response = await Ask(new FakeCapabilities(null), "user", "DeleteMe");

        response.Ok.Should().BeFalse();
        response.Refusal!.What.Should().Contain("DeleteMe");
    }

    private static Task<PluginCallResponse> Ask(
        IPluginCapabilityBroker capabilities,
        string facade,
        string member,
        string payloadJson = "{}",
        RecordingUserData? data = null
    ) =>
        new PluginBrokerService(
            PluginId,
            capabilities,
            new RecordingSecrets(),
            new RecordingBinaries(),
            new FakeServerInfo(),
            new FakeStorageRoots(),
            new RecordingLibrary(),
            new RecordingServices(),
            new RecordingServices(),
            new RecordingServices(),
            new RecordingServices(),
            new RecordingSettings(),
            data ?? new RecordingUserData()
        ).CallAsync(new PluginCallRequest(PluginId.ToString(), facade, member, payloadJson, null));

    private static IPluginCapabilityBroker Refusing() =>
        new FakeCapabilities(
            new PluginRefusal(
                PluginRefusalCodes.CapabilityNotDeclared,
                "radio",
                "The plugin read something the owner gates.",
                "It did not declare the capability that reading belongs to.",
                "Declare it in the manifest. Docs: /nomercy-plugins/handbook/capabilities",
                PluginRefusalSeverity.Blocked
            )
        );
}

internal sealed class RecordingSettings : IPluginSettings
{
    public T? Get<T>(string key) =>
        System.Text.Json.JsonSerializer.Deserialize<T>("\"BBC Radio 4\"", PluginWireJson.Options);

    public T? GetForUser<T>(string key) => Get<T>(key);

    public Task SetAsync<T>(string key, T value, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task SetForUserAsync<T>(string key, T value, CancellationToken ct = default) =>
        Task.CompletedTask;

    // Never raised: the fake exists to answer reads, and a settings change is
    // the owner's doing rather than the store's.
    public event EventHandler<string>? SettingsChanged
    {
        add { }
        remove { }
    }
}

internal sealed class RecordingUserData : IPluginUserData
{
    public int Reads { get; private set; }

    public Task<PluginUserIdentity> IdentityAsync(CancellationToken ct = default)
    {
        Reads++;

        return Task.FromResult(
            new PluginUserIdentity { Id = new UserId(Ulid.NewUlid()), DisplayName = "Stoney" }
        );
    }

    public Task<IReadOnlyList<PluginWatchEntry>> WatchAsync(CancellationToken ct = default)
    {
        Reads++;

        return Task.FromResult<IReadOnlyList<PluginWatchEntry>>([]);
    }

    public Task<IReadOnlyList<PluginPlaylist>> PlaylistsAsync(CancellationToken ct = default)
    {
        Reads++;

        return Task.FromResult<IReadOnlyList<PluginPlaylist>>([]);
    }

    public Task<PluginUserPreferences> PreferencesAsync(CancellationToken ct = default)
    {
        Reads++;

        return Task.FromResult(new PluginUserPreferences { Locale = "en" });
    }
}
