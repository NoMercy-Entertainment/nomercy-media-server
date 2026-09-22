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

using System.Text.Json;
using FluentAssertions;
using NoMercy.Plugins.Abstractions;
using NoMercy.Plugins.Capabilities;
using NoMercy.Plugins.Ipc;
using NoMercy.Plugins.OutOfProcess;
using NoMercy.Plugins.Runtime;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// The server's half of <c>process.spawn</c>: it says which file, and starts
/// nothing.
/// <para>
/// A process the server started would be a child of the server, holding the
/// server's rights and sitting beside the sandbox instead of inside it. The
/// answer is a path, and the plugin's own confined process is what runs it.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public class ProcessSpawnBrokerTests
{
    private static readonly Ulid PluginId = Ulid.NewUlid();

    /// <summary>
    /// The capability is checked before the binary is resolved. A resolver
    /// that ran first would tell a plugin without the capability whether a
    /// file exists on the owner's machine.
    /// </summary>
    [Fact]
    public async Task ASpawnWithoutTheCapability_IsRefusedAndNothingIsResolved()
    {
        RecordingBinaries binaries = new("C:/ffmpeg/ffmpeg.exe");
        PluginBrokerService service = Build(Refusing(), binaries);

        PluginCallResponse response = await Spawn(service, "ffmpeg");

        response.Ok.Should().BeFalse();
        response.Refusal!.Code.Should().Be(PluginRefusalCodes.CapabilityNotDeclared);
        binaries.Lookups.Should().Be(0);
    }

    /// <summary>
    /// The owner consents to a binary, not to child processes in general, so
    /// the name travels as the scope the consent was recorded against.
    /// </summary>
    [Fact]
    public async Task TheBinaryName_IsTheScopeTheCapabilityIsCheckedWith()
    {
        ScopeRecordingCapabilities capabilities = new();
        PluginBrokerService service = Build(capabilities, new RecordingBinaries("/usr/bin/ffmpeg"));

        await Spawn(service, "ffmpeg");

        capabilities.Capability.Should().Be(PluginCapabilityNames.ProcessSpawn);
        capabilities.Scope.Should().Be("ffmpeg");
    }

    /// <summary>
    /// A name the owner never approved resolves to nothing, and nothing is
    /// what runs. Falling back to a PATH search here is how a plugin runs
    /// whichever ffmpeg came first on a machine where PATH has been edited.
    /// </summary>
    [Fact]
    public async Task ABinaryTheOwnerNeverApproved_IsRefusedByName()
    {
        PluginBrokerService service = Build(Allowing(), new RecordingBinaries(null));

        PluginCallResponse response = await Spawn(service, "curl");

        response.Ok.Should().BeFalse();
        response.Refusal!.Code.Should().Be(PluginRefusalCodes.ProcessSpawnUndeclared);
        response.Refusal.What.Should().Contain("curl");
    }

    [Fact]
    public async Task AnApprovedBinary_AnswersTheExactFileAndStartsNothing()
    {
        RecordingBinaries binaries = new("/opt/plugins/radio/ffmpeg");
        PluginBrokerService service = Build(Allowing(), binaries);

        PluginCallResponse response = await Spawn(service, "ffmpeg");

        response.Ok.Should().BeTrue();

        PluginSpawnPermit permit = JsonSerializer.Deserialize<PluginSpawnPermit>(
            response.PayloadJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
        )!;

        permit.ResolvedPath.Should().Be("/opt/plugins/radio/ffmpeg");
        binaries.Lookups.Should().Be(1);
    }

    /// <summary>
    /// A member the facade does not have is refused by name rather than
    /// ignored, so a child process cannot reach whatever the server happens
    /// to have registered by spelling something plausible.
    /// </summary>
    [Fact]
    public async Task AProcessMemberTheFacadeDoesNotHave_IsRefusedByName()
    {
        PluginBrokerService service = Build(Allowing(), new RecordingBinaries("/usr/bin/ffmpeg"));

        PluginCallResponse response = await service.CallAsync(
            new PluginCallRequest(PluginId.ToString(), "process", "KillEverything", "{}", null)
        );

        response.Ok.Should().BeFalse();
        response.Refusal!.What.Should().Contain("KillEverything");
    }

    private static Task<PluginCallResponse> Spawn(PluginBrokerService service, string binary) =>
        service.CallAsync(
            new PluginCallRequest(
                PluginId.ToString(),
                "process",
                nameof(NoMercy.Plugins.Abstractions.IPluginProcess.SpawnAsync),
                $$"""{"binary":"{{binary}}"}""",
                null
            )
        );

    private static PluginBrokerService Build(
        IPluginCapabilityBroker capabilities,
        IPluginApprovedBinaries binaries
    ) => new(PluginId, capabilities, new RecordingSecrets(), binaries, new FakeServerInfo());

    private static IPluginCapabilityBroker Allowing() => new FakeCapabilities(null);

    private static IPluginCapabilityBroker Refusing() =>
        new FakeCapabilities(
            new PluginRefusal(
                PluginRefusalCodes.CapabilityNotDeclared,
                "radio",
                "The plugin started a child process.",
                "It did not declare the process.spawn capability.",
                "Declare it in the manifest. Docs: /nomercy-plugins/capabilities/process-spawn",
                PluginRefusalSeverity.Blocked
            )
        );
}

internal sealed class RecordingBinaries(string? path = null) : IPluginApprovedBinaries
{
    public int Lookups { get; private set; }

    public string? PathFor(Ulid pluginId, string name)
    {
        Lookups++;

        return path;
    }
}

internal sealed class ScopeRecordingCapabilities : IPluginCapabilityBroker
{
    public string? Capability { get; private set; }

    public string? Scope { get; private set; }

    public PluginRefusal? Check(Ulid pluginId, string capability, string? scopeValue = null)
    {
        Capability = capability;
        Scope = scopeValue;

        return null;
    }
}
