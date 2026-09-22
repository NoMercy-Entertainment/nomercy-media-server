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
/// The server side of the channel, and the trust boundary of the whole phase.
/// <para>
/// The plugin process is not trusted: it names a facade, a member and a plugin
/// id, and every one of those is a claim. The capability check runs before the
/// facade is touched, because a facade that ran and then refused has already
/// done the thing the refusal was for.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public class PluginBrokerServiceTests
{
    private static readonly Ulid PluginId = Ulid.NewUlid();

    [Fact]
    public async Task ACallWithoutTheCapability_IsRefusedAndTheFacadeIsNeverReached()
    {
        RecordingSecrets secrets = new();
        PluginBrokerService service = Build(Refusing(), secrets);

        PluginCallResponse response = await service.CallAsync(
            new PluginCallRequest(
                PluginId.ToString(),
                "secrets",
                "GetAsync",
                """{"key":"t"}""",
                null
            )
        );

        response.Ok.Should().BeFalse();
        response.Refusal!.Code.Should().Be(PluginRefusalCodes.CapabilityNotDeclared);
        secrets.Reads.Should().Be(0);
    }

    [Fact]
    public async Task ACallWithTheCapability_ReachesTheFacadeAndReturnsItsValue()
    {
        RecordingSecrets secrets = new("a-secret");
        PluginBrokerService service = Build(Allowing(), secrets);

        PluginCallResponse response = await service.CallAsync(
            new PluginCallRequest(
                PluginId.ToString(),
                "secrets",
                "GetAsync",
                """{"key":"t"}""",
                null
            )
        );

        response.Ok.Should().BeTrue();
        response.PayloadJson.Should().Be("\"a-secret\"");
        secrets.Reads.Should().Be(1);
    }

    /// <summary>
    /// A facade the server does not serve is refused by name rather than
    /// ignored. Dispatch by anything a caller can spell would let the child
    /// process reach whatever the server happens to have registered.
    /// </summary>
    [Fact]
    public async Task AFacadeTheServerDoesNotServe_IsRefusedByName()
    {
        PluginBrokerService service = Build(Allowing(), new RecordingSecrets());

        PluginCallResponse response = await service.CallAsync(
            new PluginCallRequest(PluginId.ToString(), "filesystem", "DeleteEverything", "{}", null)
        );

        response.Ok.Should().BeFalse();
        response.Refusal!.What.Should().Contain("filesystem");
    }

    /// <summary>
    /// The id on the wire is a claim by the child process. The broker serves
    /// one plugin and answers for that one only, or a compromised child could
    /// borrow another plugin's capabilities by typing its id.
    /// </summary>
    [Fact]
    public async Task ACallClaimingAnotherPluginsId_IsRefused()
    {
        RecordingSecrets secrets = new();
        PluginBrokerService service = Build(Allowing(), secrets);

        PluginCallResponse response = await service.CallAsync(
            new PluginCallRequest(
                Ulid.NewUlid().ToString(),
                "secrets",
                "GetAsync",
                """{"key":"t"}""",
                null
            )
        );

        response.Ok.Should().BeFalse();
        secrets.Reads.Should().Be(0);
    }

    private static PluginBrokerService Build(
        IPluginCapabilityBroker capabilities,
        IPluginSecretStore secrets
    ) => new(PluginId, capabilities, secrets, new RecordingBinaries(), new FakeServerInfo());

    private static IPluginCapabilityBroker Allowing() => new FakeCapabilities(null);

    private static IPluginCapabilityBroker Refusing() =>
        new FakeCapabilities(
            new PluginRefusal(
                PluginRefusalCodes.CapabilityNotDeclared,
                "radio",
                "The plugin asked for a secret.",
                "It did not declare the secrets capability.",
                "Declare it in the manifest. Docs: /nomercy-plugins/handbook/capabilities",
                PluginRefusalSeverity.Blocked
            )
        );
}

internal sealed class FakeCapabilities(PluginRefusal? refusal) : IPluginCapabilityBroker
{
    public PluginRefusal? Check(Ulid pluginId, string capability, string? scopeValue = null) =>
        refusal;
}

internal sealed class RecordingSecrets(string? value = null) : IPluginSecretStore
{
    public int Reads { get; private set; }

    public Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        Reads++;
        return Task.FromResult(value);
    }

    public Task SetAsync(string key, string v, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task DeleteAsync(string key, CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<string>> KeysAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    public Task<string?> GetForUserAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(value);

    public Task SetForUserAsync(string key, string v, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task DeleteForUserAsync(string key, CancellationToken ct = default) =>
        Task.CompletedTask;
}
