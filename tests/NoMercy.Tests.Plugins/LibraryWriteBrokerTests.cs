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
/// The two library facades that change something, across the boundary.
/// <para>
/// Nothing here hands the plugin's own process a file. A delete the plugin
/// performed itself would be a delete the broker never gated, and the server
/// owns the library roots and the recycle bin.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public class LibraryWriteBrokerTests
{
    private static readonly Ulid PluginId = Ulid.NewUlid();

    private const string ImportPayload =
        """{"request":{"library":"01JBQ0Y1ZQ8W4T7N2M6K5R3H9A","kind":"Movie","sourcePath":"D:/in/one.mkv"}}""";

    [Fact]
    public async Task ADeleteWithoutTheCapability_IsRefusedAndNothingIsDeleted()
    {
        RecordingWriter writer = new();

        PluginCallResponse response = await Ask(
            Refusing(PluginCapabilityNames.LibraryWrite),
            "libraryWriter",
            nameof(IPluginLibraryWriter.DeleteAsync),
            """{"path":"D:/films/one.mkv"}""",
            writer
        );

        response.Ok.Should().BeFalse();
        response.Refusal!.Code.Should().Be(PluginRefusalCodes.CapabilityNotDeclared);
        writer.Deleted.Should().BeNull();
    }

    [Fact]
    public async Task ThePathReachesTheDelete()
    {
        RecordingWriter writer = new();

        await Ask(
            new FakeCapabilities(null),
            "libraryWriter",
            nameof(IPluginLibraryWriter.DeleteAsync),
            """{"path":"D:/films/one.mkv"}""",
            writer
        );

        writer.Deleted.Should().Be("D:/films/one.mkv");
    }

    /// <summary>
    /// A move carries two paths, and a proxy that sent only one would move the
    /// file onto itself or onto nothing.
    /// </summary>
    [Fact]
    public async Task BothEndsOfAMoveReachTheWriter()
    {
        RecordingWriter writer = new();

        await Ask(
            new FakeCapabilities(null),
            "libraryWriter",
            nameof(IPluginLibraryWriter.MoveAsync),
            """{"path":"D:/in/one.mkv","destinationPath":"D:/films/one.mkv"}""",
            writer
        );

        writer.MovedFrom.Should().Be("D:/in/one.mkv");
        writer.MovedTo.Should().Be("D:/films/one.mkv");
    }

    [Fact]
    public async Task TheWritableLibrariesCrossBackAsRows()
    {
        PluginCallResponse response = await Ask(
            new FakeCapabilities(null),
            "libraryWriter",
            nameof(IPluginLibraryWriter.GetWritableLibrariesAsync),
            "{}",
            new RecordingWriter()
        );

        response.Ok.Should().BeTrue();
        response.PayloadJson.Should().Contain("Films");
    }

    /// <summary>
    /// The capability can be granted on an install whose host carries no writer
    /// at all. That reads as a fact about the install rather than as a plugin
    /// that asked for something it never declared.
    /// </summary>
    [Fact]
    public async Task AGrantedCapabilityWithNoWriterOnTheHost_SaysTheFacadeIsAbsent()
    {
        PluginCallResponse response = await Ask(
            new FakeCapabilities(null),
            "libraryWriter",
            nameof(IPluginLibraryWriter.DeleteAsync),
            """{"path":"D:/films/one.mkv"}"""
        );

        response.Ok.Should().BeFalse();
        response.Refusal!.What.Should().Contain("LibraryWriter");
    }

    [Fact]
    public async Task AnImportWithoutTheCapability_IsRefusedAndNothingIsRegistered()
    {
        RecordingImport import = new();

        PluginCallResponse response = await Ask(
            Refusing(PluginCapabilityNames.LibraryImport),
            "libraryImport",
            nameof(IPluginLibraryImport.RegisterAsync),
            ImportPayload,
            import: import
        );

        response.Ok.Should().BeFalse();
        import.Registered.Should().BeNull();
    }

    [Fact]
    public async Task TheSourcePathReachesTheImport()
    {
        RecordingImport import = new();

        await Ask(
            new FakeCapabilities(null),
            "libraryImport",
            nameof(IPluginLibraryImport.RegisterAsync),
            ImportPayload,
            import: import
        );

        import.Registered!.SourcePath.Should().Be("D:/in/one.mkv");
    }

    /// <summary>
    /// A recording still being written goes through StreamAsync, and a broker
    /// that routed both members to the same one would finish the import before
    /// the program had finished airing.
    /// </summary>
    [Fact]
    public async Task StreamAndRegisterAreNotTheSameCall()
    {
        RecordingImport import = new();

        await Ask(
            new FakeCapabilities(null),
            "libraryImport",
            nameof(IPluginLibraryImport.StreamAsync),
            ImportPayload,
            import: import
        );

        import.Streamed!.SourcePath.Should().Be("D:/in/one.mkv");
        import.Registered.Should().BeNull();
    }

    [Fact]
    public async Task AnImportWithNoRequestOnIt_SaysThePayloadWasNotUnderstood()
    {
        RecordingImport import = new();

        PluginCallResponse response = await Ask(
            new FakeCapabilities(null),
            "libraryImport",
            nameof(IPluginLibraryImport.RegisterAsync),
            "{}",
            import: import
        );

        response.Ok.Should().BeFalse();
        response.Refusal!.Why.Should().Contain("shape");
        import.Registered.Should().BeNull();
    }

    /// <summary>
    /// Arguments in a shape the server cannot read are a refusal, not a
    /// channel that stopped answering. Thrown across the boundary the plugin
    /// author would read nothing at all.
    /// </summary>
    [Fact]
    public async Task ArgumentsTheServerCannotRead_ComeBackAsARefusalRatherThanADeadChannel()
    {
        RecordingImport import = new();

        PluginCallResponse response = await Ask(
            new FakeCapabilities(null),
            "libraryImport",
            nameof(IPluginLibraryImport.RegisterAsync),
            """{"request":{"library":{"nested":"not an id"},"kind":"Movie","sourcePath":"x"}}""",
            import: import
        );

        response.Ok.Should().BeFalse();
        response.Refusal!.What.Should().Contain("libraryImport");
        import.Registered.Should().BeNull();
    }

    private static Task<PluginCallResponse> Ask(
        IPluginCapabilityBroker capabilities,
        string facade,
        string member,
        string payloadJson,
        IPluginLibraryWriter? writer = null,
        IPluginLibraryImport? import = null
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
            new RecordingUserData(),
            writer,
            import
        ).CallAsync(new PluginCallRequest(PluginId.ToString(), facade, member, payloadJson, null));

    private static IPluginCapabilityBroker Refusing(string capability) =>
        new FakeCapabilities(
            new PluginRefusal(
                PluginRefusalCodes.CapabilityNotDeclared,
                "radio",
                "The plugin changed the owner's library.",
                $"It did not declare the {capability} capability.",
                "Declare it in the manifest. Docs: /nomercy-plugins/capabilities/library-write",
                PluginRefusalSeverity.Blocked
            )
        );
}

internal sealed class RecordingWriter : IPluginLibraryWriter
{
    public string? Deleted { get; private set; }

    public string? Recycled { get; private set; }

    public string? MovedFrom { get; private set; }

    public string? MovedTo { get; private set; }

    public Task<IReadOnlyList<PluginLibrary>> GetWritableLibrariesAsync(
        CancellationToken ct = default
    ) =>
        Task.FromResult<IReadOnlyList<PluginLibrary>>([
            new PluginLibrary("films", "Films", "movie"),
        ]);

    public Task RecycleAsync(string path, CancellationToken ct = default)
    {
        Recycled = path;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string path, CancellationToken ct = default)
    {
        Deleted = path;
        return Task.CompletedTask;
    }

    public Task MoveAsync(string sourcePath, string destinationPath, CancellationToken ct = default)
    {
        MovedFrom = sourcePath;
        MovedTo = destinationPath;
        return Task.CompletedTask;
    }

    public Task<bool> CanWriteAsync(string path, CancellationToken ct = default) =>
        Task.FromResult(true);
}

internal sealed class RecordingImport : IPluginLibraryImport
{
    public PluginImportRequest? Registered { get; private set; }

    public PluginImportRequest? Streamed { get; private set; }

    public Task<PluginImportResult> RegisterAsync(
        PluginImportRequest request,
        CancellationToken ct = default
    )
    {
        Registered = request;
        return Task.FromResult(new PluginImportResult(new MediaId(Ulid.NewUlid()), null));
    }

    public Task<PluginImportResult> StreamAsync(
        PluginImportRequest request,
        CancellationToken ct = default
    )
    {
        Streamed = request;
        return Task.FromResult(new PluginImportResult(new MediaId(Ulid.NewUlid()), null));
    }
}
