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
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Capabilities;
using NoMercy.PluginSdk.Ipc;
using NoMercy.PluginSdk.OutOfProcess;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// The server's half of storage: which folder, never the bytes in it.
/// <para>
/// The plugin's own three folders need no grant, because the server made them
/// for this plugin and nothing else can reach them. One of the owner's folders
/// needs the capability and a grant naming that folder, and the check runs
/// before the path is looked up.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public class StorageBrokerTests
{
    private static readonly Ulid PluginId = Ulid.NewUlid();

    [Fact]
    public async Task ThePluginsOwnFolderIsAnsweredWithoutAnyGrant()
    {
        FakeStorageRoots roots = new();

        PluginCallResponse response = await Ask(Refusing(), roots, nameof(IPluginStorage.Private));

        response.Ok.Should().BeTrue();
        response.PayloadJson.Should().Contain("private-root");
    }

    /// <summary>
    /// The capability is checked before the folder is looked up. A lookup that
    /// ran first would tell a plugin without the grant whether a folder exists
    /// on the owner's machine.
    /// </summary>
    [Fact]
    public async Task AnOwnersFolderWithoutTheCapability_IsRefusedAndNothingIsLookedUp()
    {
        FakeStorageRoots roots = new();

        PluginCallResponse response = await Ask(
            Refusing(),
            roots,
            nameof(IPluginStorage.PathAsync),
            """{"folderId":"movies"}"""
        );

        response.Ok.Should().BeFalse();
        response.Refusal!.Code.Should().Be(PluginRefusalCodes.CapabilityNotDeclared);
        roots.Lookups.Should().Be(0);
    }

    /// <summary>
    /// The owner consents to a folder, not to the file system, so the folder
    /// id travels as the scope the consent was recorded against.
    /// </summary>
    [Fact]
    public async Task TheFolderIdIsTheScopeTheCapabilityIsCheckedWith()
    {
        ScopeRecordingCapabilities capabilities = new();

        await Ask(
            capabilities,
            new FakeStorageRoots(),
            nameof(IPluginStorage.PathAsync),
            """{"folderId":"movies"}"""
        );

        capabilities.Capability.Should().Be(PluginCapabilityNames.StoragePath);
        capabilities.Scope.Should().Be("movies");
    }

    [Fact]
    public async Task AFolderWithNoGrantResolvesToNothingAndIsRefusedByName()
    {
        PluginCallResponse response = await Ask(
            new FakeCapabilities(null),
            new FakeStorageRoots(resolves: false),
            nameof(IPluginStorage.PathAsync),
            """{"folderId":"movies"}"""
        );

        response.Ok.Should().BeFalse();
        response.Refusal!.Code.Should().Be(PluginRefusalCodes.FileOutsideGrant);
        response.Refusal.What.Should().Contain("movies");
    }

    [Fact]
    public async Task AGrantedFolderIsAnsweredAsAPathAndNotAsItsContents()
    {
        PluginCallResponse response = await Ask(
            new FakeCapabilities(null),
            new FakeStorageRoots(),
            nameof(IPluginStorage.PathAsync),
            """{"folderId":"movies"}"""
        );

        response.Ok.Should().BeTrue();
        response.PayloadJson.Should().Be("\"/media/movies\"");
    }

    [Fact]
    public async Task AStorageMemberTheFacadeDoesNotHave_IsRefusedByName()
    {
        PluginCallResponse response = await Ask(
            new FakeCapabilities(null),
            new FakeStorageRoots(),
            "DeleteEverything"
        );

        response.Ok.Should().BeFalse();
        response.Refusal!.What.Should().Contain("DeleteEverything");
    }

    private static Task<PluginCallResponse> Ask(
        IPluginCapabilityBroker capabilities,
        IPluginStorageRoots roots,
        string member,
        string payloadJson = "{}"
    ) =>
        new PluginBrokerService(
            PluginId,
            capabilities,
            new RecordingSecrets(),
            new RecordingBinaries(),
            new FakeServerInfo(),
            roots,
            new RecordingLibrary(),
            new RecordingServices(),
            new RecordingServices(),
            new RecordingServices(),
            new RecordingServices(),
            new RecordingSettings(),
            new RecordingUserData()
        ).CallAsync(
            new PluginCallRequest(PluginId.ToString(), "storage", member, payloadJson, null)
        );

    private static IPluginCapabilityBroker Refusing() =>
        new FakeCapabilities(
            new PluginRefusal(
                PluginRefusalCodes.CapabilityNotDeclared,
                "radio",
                "The plugin reached a folder.",
                "It did not declare the storage.path capability.",
                "Declare it in the manifest. Docs: /nomercy-plugins/capabilities/storage",
                PluginRefusalSeverity.Blocked
            )
        );
}

internal sealed class FakeStorageRoots(bool resolves = true) : IPluginStorageRoots
{
    public int Lookups { get; private set; }

    public string PrivateRoot => "/data/private-root";

    public string TempRoot => "/data/temp-root";

    public string DerivedRoot => "/data/derived-root";

    public Task<string?> PathForAsync(string folderId, CancellationToken ct = default)
    {
        Lookups++;

        return Task.FromResult(resolves ? "/media/movies" : null);
    }
}
