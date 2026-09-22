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
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Capabilities;
using NoMercy.PluginSdk.Ipc;
using NoMercy.PluginSdk.OutOfProcess;
using NoMercy.PluginSdk.Runtime;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// The last two facades to cross: native code, which answers a path and loads
/// nothing, and the media tree, whose five branches each carry their own
/// capability.
/// </summary>
[Trait("Category", "Unit")]
public class NativeMediaBrokerTests : IDisposable
{
    private static readonly Ulid PluginId = Ulid.NewUlid();

    private readonly string _bundle = Directory.CreateTempSubdirectory("plugin-native-").FullName;

    public void Dispose() => Directory.Delete(_bundle, recursive: true);

    private string Place(string library)
    {
        string name =
            OperatingSystem.IsWindows() ? $"{library}.dll"
            : OperatingSystem.IsMacOS() ? $"lib{library}.dylib"
            : $"lib{library}.so";
        string path = Path.Combine(_bundle, name);
        File.WriteAllBytes(path, [1, 2, 3]);
        return path;
    }

    /// <summary>
    /// The broker answers which file and loads nothing. A library loaded here
    /// would land in the server's own load context, where the plugin's
    /// DllImport declarations never look, and would run with the server's
    /// rights rather than inside the sandbox.
    /// </summary>
    [Fact]
    public async Task ASignedLibraryComesBackAsAPathRatherThanALoadedHandle()
    {
        string expected = Place("codec");

        PluginCallResponse response = await Ask(
            new FakeCapabilities(null),
            "native",
            nameof(IPluginNative.LoadAsync),
            """{"library":"codec"}"""
        );

        response.Ok.Should().BeTrue();
        response.PayloadJson.Should().Contain(Path.GetFileName(expected));
    }

    [Fact]
    public async Task AnUnsignedBundleIsRefusedBeforeAnyPathIsResolved()
    {
        Place("codec");

        PluginCallResponse response = await Ask(
            new FakeCapabilities(null),
            "native",
            nameof(IPluginNative.LoadAsync),
            """{"library":"codec"}""",
            signed: false
        );

        response.Ok.Should().BeFalse();
        response.Refusal!.Code.Should().Be(PluginRefusalCodes.NativeCodeUnsigned);
    }

    /// <summary>
    /// A bare name, never a path. The signature says the marketplace built the
    /// bundle, and says nothing about a file reached from outside it.
    /// </summary>
    [Theory]
    [InlineData("../../system32/kernel32")]
    [InlineData("sub/codec")]
    [InlineData(@"sub\codec")]
    [InlineData("C:codec")]
    public async Task ANameThatIsReallyAPathIsRefused(string library)
    {
        PluginCallResponse response = await Ask(
            new FakeCapabilities(null),
            "native",
            nameof(IPluginNative.LoadAsync),
            $$"""{"library":"{{library.Replace(@"\", @"\\")}}"}"""
        );

        response.Ok.Should().BeFalse();
        response.Refusal!.Code.Should().Be(PluginRefusalCodes.FileOutsideGrant);
    }

    [Fact]
    public async Task ALibraryWithoutTheCapabilityIsRefused()
    {
        Place("codec");

        PluginCallResponse response = await Ask(
            Refusing(),
            "native",
            nameof(IPluginNative.LoadAsync),
            """{"library":"codec"}"""
        );

        response.Ok.Should().BeFalse();
        response.Refusal!.Code.Should().Be(PluginRefusalCodes.CapabilityNotDeclared);
    }

    /// <summary>
    /// Transcode and Remux both declare StartAsync, so the branch has to be on
    /// the wire. One shared "media" name would leave the broker guessing which
    /// of the two capabilities to check, and a plugin granted only one of them
    /// would get both.
    /// </summary>
    [Fact]
    public async Task TranscodeAndRemuxAreSeparateNamesOnTheWire()
    {
        RecordingMedia media = new();

        PluginCallResponse response = await Ask(
            new FakeCapabilities(null),
            "media.remux",
            nameof(IPluginMediaRemux.StartAsync),
            RemuxPayload,
            media: media
        );

        response.Ok.Should().BeTrue();

        media.Remuxed.Should().NotBeNull();
        media.Transcoded.Should().BeNull();
    }

    [Fact]
    public async Task TheRemuxSourceReachesTheServer()
    {
        RecordingMedia media = new();

        PluginCallResponse response = await Ask(
            new FakeCapabilities(null),
            "media.transcode",
            nameof(IPluginMediaTranscode.StartAsync),
            RemuxPayload,
            media: media
        );

        response.Ok.Should().BeTrue();

        media.Transcoded!.Source.Should().Be(new Uri("https://example.test/one.ts"));
    }

    [Fact]
    public async Task AMintedUrlSurvivesTheWire()
    {
        PluginCallResponse response = await Ask(
            new FakeCapabilities(null),
            "media.remux",
            nameof(IPluginMediaRemux.StartAsync),
            RemuxPayload,
            media: new RecordingMedia()
        );

        response.Ok.Should().BeTrue();
        response.PayloadJson.Should().Contain("minted.test");
    }

    [Fact]
    public async Task TheChannelsReachTheLiveBranch()
    {
        RecordingMedia media = new();

        PluginCallResponse response = await Ask(
            new FakeCapabilities(null),
            "media.live",
            nameof(IPluginMediaLive.PublishAsync),
            """{"channels":[{"id":"one","name":"One","links":[],"streamKind":"Hls"}]}""",
            media: media
        );

        response.Ok.Should().BeTrue();

        media.Channels.Should().ContainSingle();
    }

    [Fact]
    public async Task TheRecordingsComeBackAsRows()
    {
        PluginCallResponse response = await Ask(
            new FakeCapabilities(null),
            "media.record",
            nameof(IPluginRecorder.ListAsync),
            "{}",
            media: new RecordingMedia()
        );

        response.Ok.Should().BeTrue();
    }

    /// <summary>
    /// The capability can be granted on an install whose host carries no media
    /// tree at all. That reads as a fact about the install rather than as a
    /// plugin that asked for something it never declared.
    /// </summary>
    [Fact]
    public async Task AGrantedCapabilityWithNoMediaOnTheHost_SaysTheFacadeIsAbsent()
    {
        PluginCallResponse response = await Ask(
            new FakeCapabilities(null),
            "media.remux",
            nameof(IPluginMediaRemux.StartAsync),
            RemuxPayload
        );

        response.Ok.Should().BeFalse();
        response.Refusal!.What.Should().Contain("Media");
    }

    private const string RemuxPayload =
        """{"request":{"source":"https://example.test/one.ts","container":"Hls"}}""";

    private Task<PluginCallResponse> Ask(
        IPluginCapabilityBroker capabilities,
        string facade,
        string member,
        string payloadJson,
        bool signed = true,
        IPluginMedia? media = null
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
            null,
            null,
            new FakeSignature(signed),
            _bundle,
            media
        ).CallAsync(new PluginCallRequest(PluginId.ToString(), facade, member, payloadJson, null));

    private static IPluginCapabilityBroker Refusing() =>
        new FakeCapabilities(
            new PluginRefusal(
                PluginRefusalCodes.CapabilityNotDeclared,
                "radio",
                "The plugin loaded a native library.",
                "It did not declare the native.code capability.",
                "Declare it in the manifest. Docs: /nomercy-plugins/capabilities/native-code",
                PluginRefusalSeverity.Blocked
            )
        );
}

internal sealed class FakeSignature(bool signed) : IPluginBundleSignature
{
    public bool IsMarketplaceSigned(Ulid pluginId) => signed;
}

internal sealed class RecordingMedia : IPluginMedia
{
    private readonly RecordingBranches _branches = new();

    public IPluginMediaProxy Proxy => _branches;

    public IPluginMediaTranscode Transcode => _branches;

    public IPluginMediaRemux Remux => _branches;

    public IPluginMediaLive Live => _branches;

    public IPluginRecorder Record => _branches;

    public PluginRemuxRequest? Remuxed => _branches.Remuxed;

    public PluginRemuxRequest? Transcoded => _branches.Transcoded;

    public IReadOnlyList<PluginLiveChannel> Channels => _branches.Channels;
}

internal sealed class RecordingBranches
    : IPluginMediaProxy,
        IPluginMediaTranscode,
        IPluginMediaRemux,
        IPluginMediaLive,
        IPluginRecorder
{
    public PluginRemuxRequest? Remuxed { get; private set; }

    public PluginRemuxRequest? Transcoded { get; private set; }

    public IReadOnlyList<PluginLiveChannel> Channels { get; private set; } = [];

    /// <summary>
    /// Built the way one arrives from the server: through the serializer. The
    /// setters are internal so a plugin cannot forge one, and the wire has to
    /// be able to fill them anyway.
    /// </summary>
    private static PluginMediaUrl Url =>
        JsonSerializer.Deserialize<PluginMediaUrl>(
            """{"url":"https://minted.test/one.m3u8","expiresAt":"2030-01-01T00:00:00+00:00","media":"01JBQ0Y1ZQ8W4T7N2M6K5R3H9A"}""",
            PluginWireJson.Options
        )!;

    public Task<PluginMediaUrl> MintAsync(
        PluginProxyRequest request,
        CancellationToken ct = default
    ) => Task.FromResult(Url);

    public Task<PluginMediaUrl> MintImageAsync(Uri source, CancellationToken ct = default) =>
        Task.FromResult(Url);

    Task<PluginMediaUrl> IPluginMediaTranscode.StartAsync(
        PluginRemuxRequest request,
        CancellationToken ct
    )
    {
        Transcoded = request;
        return Task.FromResult(Url);
    }

    Task<PluginMediaUrl> IPluginMediaRemux.StartAsync(
        PluginRemuxRequest request,
        CancellationToken ct
    )
    {
        Remuxed = request;
        return Task.FromResult(Url);
    }

    public Task PublishAsync(
        IReadOnlyList<PluginLiveChannel> channels,
        CancellationToken ct = default
    )
    {
        Channels = channels;
        return Task.CompletedTask;
    }

    public Task PublishGuideAsync(
        IReadOnlyList<PluginEpgProgram> guide,
        CancellationToken ct = default
    ) => Task.CompletedTask;

    public Task PublishGroupsAsync(
        IReadOnlyList<PluginChannelGroup> groups,
        CancellationToken ct = default
    ) => Task.CompletedTask;

    public Task<JobId> ScheduleAsync(
        PluginRecordingRequest request,
        CancellationToken ct = default
    ) => Task.FromResult(new JobId(Ulid.NewUlid()));

    public Task CancelAsync(JobId recording, CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<PluginRecording>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PluginRecording>>([]);
}
