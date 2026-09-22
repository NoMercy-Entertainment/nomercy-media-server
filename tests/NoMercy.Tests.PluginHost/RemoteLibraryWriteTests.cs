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
using NoMercy.PluginHost;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Ipc;
using Xunit;

namespace NoMercy.Tests.PluginHost;

/// <summary>
/// The four facades that finish the boundary, as the plugin process sees them:
/// writing to the library, importing into it, loading native code, and the
/// media tree.
/// </summary>
[Trait("Category", "Unit")]
public class RemoteLibraryWriteTests
{
    private static readonly Ulid PluginId = Ulid.NewUlid();

    [Fact]
    public async Task ADeleteCrossesAsTheFacadeAndMemberTheServerRoutesOn()
    {
        RecordingBroker broker = new(PluginCallResponse.Value("null"));
        RemoteLibraryWriter writer = new(new RemoteCall(PluginId, broker));

        await writer.DeleteAsync("D:/films/one.mkv");

        broker.Last!.Facade.Should().Be("libraryWriter");
        broker.Last!.Member.Should().Be(nameof(IPluginLibraryWriter.DeleteAsync));
        broker.Last!.PayloadJson.Should().Contain("one.mkv");
    }

    /// <summary>
    /// A move carries two paths. Sending one would move the file onto itself
    /// or onto nothing, and the server cannot tell the difference.
    /// </summary>
    [Fact]
    public async Task BothEndsOfAMoveCrossTheBoundary()
    {
        RecordingBroker broker = new(PluginCallResponse.Value("null"));
        RemoteLibraryWriter writer = new(new RemoteCall(PluginId, broker));

        await writer.MoveAsync("D:/in/one.mkv", "D:/films/one.mkv");

        broker.Last!.PayloadJson.Should().Contain("D:/in/one.mkv");
        broker.Last!.PayloadJson.Should().Contain("D:/films/one.mkv");
    }

    [Fact]
    public async Task ARefusedDeleteReachesThePluginAsARefusalRatherThanAQuietNoOp()
    {
        RecordingBroker broker = new(
            PluginCallResponse.Refused(
                new WireRefusal(
                    PluginRefusalCodes.CapabilityNotDeclared,
                    PluginId.ToString(),
                    "The plugin deleted a file.",
                    "It did not declare the library.write capability.",
                    "Declare it in the manifest.",
                    PluginRefusalSeverity.Blocked.ToString()
                )
            )
        );
        RemoteLibraryWriter writer = new(new RemoteCall(PluginId, broker));

        await Assert.ThrowsAsync<PluginRefusedException>(() =>
            writer.DeleteAsync("D:/films/one.mkv")
        );
    }

    /// <summary>
    /// A recording still being written goes through StreamAsync, and a proxy
    /// that sent both members under one name would finish the import before
    /// the program had finished airing.
    /// </summary>
    [Fact]
    public async Task StreamingAnImportIsNotTheSameCallAsRegisteringOne()
    {
        RecordingBroker broker = new(
            PluginCallResponse.Value("""{"media":"01JBQ0Y1ZQ8W4T7N2M6K5R3H9A","refusal":null}""")
        );
        RemoteLibraryImport import = new(PluginId, new RemoteCall(PluginId, broker));

        await import.StreamAsync(Request());

        broker.Last!.Facade.Should().Be("libraryImport");
        broker.Last!.Member.Should().Be(nameof(IPluginLibraryImport.StreamAsync));
    }

    /// <summary>
    /// The server answering nothing where an import result belongs must not
    /// surface as a NullReferenceException inside the plugin's own code.
    /// </summary>
    [Fact]
    public async Task AnImportTheServerAnsweredEmptyComesBackAsARefusalNotANullReference()
    {
        RecordingBroker broker = new(PluginCallResponse.Value("null"));
        RemoteLibraryImport import = new(PluginId, new RemoteCall(PluginId, broker));

        PluginImportResult result = await import.RegisterAsync(Request());

        result.Ok.Should().BeFalse();
        result.Refusal!.Why.Should().Contain("nothing");
    }

    /// <summary>
    /// The server resolves the file; this process performs the load. Loaded on
    /// the server it would land in the server's own load context, where the
    /// plugin's DllImport declarations never look, and run with the server's
    /// rights rather than inside the sandbox.
    /// </summary>
    [Fact]
    public async Task ANativeLibraryIsLoadedHereFromThePathTheServerResolved()
    {
        RecordingBroker broker = new(
            PluginCallResponse.Value("""{"resolvedPath":"D:/plugins/radio/codec.dll"}""")
        );
        RecordingLoader loader = new(loads: true);
        RemoteNative native = new(PluginId, new RemoteCall(PluginId, broker), loader);

        await native.LoadAsync("codec");

        loader.Loaded.Should().Be("D:/plugins/radio/codec.dll");
        native.IsLoaded("codec").Should().BeTrue();
    }

    /// <summary>
    /// A second call must not pay for the boundary again. The handle is held
    /// here, so the server has nothing left to answer.
    /// </summary>
    [Fact]
    public async Task ALibraryAlreadyLoadedIsNotAskedForTwice()
    {
        RecordingBroker broker = new(
            PluginCallResponse.Value("""{"resolvedPath":"D:/plugins/radio/codec.dll"}""")
        );
        RemoteNative native = new(
            PluginId,
            new RemoteCall(PluginId, broker),
            new RecordingLoader(loads: true)
        );

        await native.LoadAsync("codec");
        await native.LoadAsync("codec");

        broker.Calls.Should().Be(1);
    }

    /// <summary>
    /// A load that failed must not read as loaded afterwards, or the plugin's
    /// first DllImport dies with an entry-point error naming nothing.
    /// </summary>
    [Fact]
    public async Task ALoadThatFailedHereIsNotRememberedAsLoaded()
    {
        RecordingBroker broker = new(
            PluginCallResponse.Value("""{"resolvedPath":"D:/plugins/radio/codec.dll"}""")
        );
        RemoteNative native = new(
            PluginId,
            new RemoteCall(PluginId, broker),
            new RecordingLoader(loads: false)
        );

        await Assert.ThrowsAsync<PluginRefusedException>(() => native.LoadAsync("codec"));

        native.IsLoaded("codec").Should().BeFalse();
    }

    /// <summary>
    /// Transcode and Remux both declare StartAsync, so the branch has to be in
    /// the name on the wire. One shared name would leave the server guessing
    /// which of the two capabilities to check.
    /// </summary>
    [Fact]
    public async Task EachMediaBranchCrossesUnderItsOwnName()
    {
        RecordingBroker broker = new(MintedUrl);
        RemoteMedia media = new(new RemoteCall(PluginId, broker));

        await media.Remux.StartAsync(Remux());
        string remux = broker.Last!.Facade;

        await media.Transcode.StartAsync(Remux());

        remux.Should().Be("media.remux");
        broker.Last!.Facade.Should().Be("media.transcode");
    }

    /// <summary>
    /// The url's setters are internal so a plugin cannot forge one, and the
    /// wire still has to be able to fill them.
    /// </summary>
    [Fact]
    public async Task AMintedUrlArrivesWithItsAddressIntact()
    {
        RemoteMedia media = new(new RemoteCall(PluginId, new RecordingBroker(MintedUrl)));

        PluginMediaUrl url = await media.Proxy.MintImageAsync(new Uri("https://art.test/a.jpg"));

        url.Url.Should().Be(new Uri("https://minted.test/one.m3u8"));
    }

    private static PluginCallResponse MintedUrl =>
        PluginCallResponse.Value(
            """{"url":"https://minted.test/one.m3u8","expiresAt":"2030-01-01T00:00:00+00:00","media":"01JBQ0Y1ZQ8W4T7N2M6K5R3H9A"}"""
        );

    private static PluginRemuxRequest Remux() =>
        new()
        {
            Source = new Uri("https://example.test/one.ts"),
            Container = PluginRemuxContainer.Hls,
        };

    private static PluginImportRequest Request() =>
        new()
        {
            Library = new LibraryId(Ulid.NewUlid()),
            Kind = PluginMediaKind.Movie,
            SourcePath = "D:/in/one.mkv",
        };
}

internal sealed class RecordingLoader(bool loads) : ILocalNativeLoader
{
    public string? Loaded { get; private set; }

    public bool TryLoad(string path, out nint handle)
    {
        handle = loads ? 1 : 0;

        if (!loads)
            return false;

        Loaded = path;
        return true;
    }
}
