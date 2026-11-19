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
/// The server's half of library reads, the one facade whose answer is the data
/// itself rather than a permit.
/// </summary>
[Trait("Category", "Unit")]
public class LibraryBrokerTests
{
    private static readonly Ulid PluginId = Ulid.NewUlid();

    /// <summary>
    /// The capability is checked before a single row is read. A query that ran
    /// and then refused has already told the server's disk what to look for.
    /// </summary>
    [Fact]
    public async Task AReadWithoutTheCapability_IsRefusedAndNothingIsQueried()
    {
        RecordingLibrary library = new();

        PluginCallResponse response = await Ask(
            Refusing(),
            library,
            nameof(IPluginLibraryQuery.GetLibrariesAsync)
        );

        response.Ok.Should().BeFalse();
        response.Refusal!.Code.Should().Be(PluginRefusalCodes.CapabilityNotDeclared);
        library.Queries.Should().Be(0);
    }

    [Fact]
    public async Task TheLibrariesCrossBackAsRows()
    {
        PluginCallResponse response = await Ask(
            new FakeCapabilities(null),
            new RecordingLibrary(),
            nameof(IPluginLibraryQuery.GetLibrariesAsync)
        );

        response.Ok.Should().BeTrue();
        response.PayloadJson.Should().Contain("Films");
    }

    /// <summary>
    /// How a library is encoded crosses with it (ABI 12.11). A member the
    /// broker dropped would read in the plugin as a library nothing encodes.
    /// </summary>
    [Fact]
    public async Task TheEncodingPresetsCrossBackWithTheirLibrary()
    {
        PluginCallResponse response = await Ask(
            new FakeCapabilities(null),
            new RecordingLibrary(),
            nameof(IPluginLibraryQuery.GetLibrariesAsync)
        );

        List<PluginLibrary>? libraries = System.Text.Json.JsonSerializer.Deserialize<
            List<PluginLibrary>
        >(response.PayloadJson!, PluginWireJson.Options);

        libraries!
            .Should()
            .ContainSingle()
            .Which.EncodingPresets.Should()
            .Equal(RecordingLibrary.Streaming);
    }

    /// <summary>
    /// Why a show is in its library crosses with it (ABI 12.13). A member the
    /// broker dropped would read in the plugin as "the server does not say".
    /// </summary>
    [Fact]
    public async Task WhyAShowIsInItsLibraryCrossesBackWithIt()
    {
        PluginCallResponse response = await Ask(
            new FakeCapabilities(null),
            new RecordingLibrary(),
            nameof(IPluginLibraryQuery.GetShowsAsync)
        );

        List<PluginLibraryShow>? shows = System.Text.Json.JsonSerializer.Deserialize<
            List<PluginLibraryShow>
        >(response.PayloadJson!, PluginWireJson.Options);

        shows!.Should().ContainSingle().Which.AddedBy.Should().Be(PluginLibraryAddedBy.Manual);
    }

    /// <summary>
    /// A plugin asking for one library's shows must not be handed every
    /// library's. The filter is the argument, and dropping it is a plugin
    /// reading rows it did not ask for.
    /// </summary>
    [Fact]
    public async Task TheLibraryFilterReachesTheQuery()
    {
        RecordingLibrary library = new();

        await Ask(
            new FakeCapabilities(null),
            library,
            nameof(IPluginLibraryQuery.GetShowsAsync),
            """{"libraryId":"tv"}"""
        );

        library.LastLibraryId.Should().Be("tv");
    }

    [Fact]
    public async Task TheShowIdReachesTheEpisodeQuery()
    {
        RecordingLibrary library = new();

        await Ask(
            new FakeCapabilities(null),
            library,
            nameof(IPluginLibraryQuery.GetEpisodesAsync),
            """{"showId":42}"""
        );

        library.LastShowId.Should().Be(42);
    }

    [Fact]
    public async Task ALibraryMemberThatDoesNotCrossYet_SaysSoByName()
    {
        PluginCallResponse response = await Ask(
            new FakeCapabilities(null),
            new RecordingLibrary(),
            "DeleteEverything"
        );

        response.Ok.Should().BeFalse();
        response.Refusal!.What.Should().Contain("DeleteEverything");
    }

    private static Task<PluginCallResponse> Ask(
        IPluginCapabilityBroker capabilities,
        IPluginLibraryQuery library,
        string member,
        string payloadJson = "{}"
    ) =>
        new PluginBrokerService(
            PluginId,
            capabilities,
            new RecordingSecrets(),
            new RecordingBinaries(),
            new FakeServerInfo(),
            new FakeStorageRoots(),
            library,
            new RecordingServices(),
            new RecordingServices(),
            new RecordingServices(),
            new RecordingServices(),
            new RecordingSettings(),
            new RecordingUserData()
        ).CallAsync(
            new PluginCallRequest(PluginId.ToString(), "library", member, payloadJson, null)
        );

    private static IPluginCapabilityBroker Refusing() =>
        new FakeCapabilities(
            new PluginRefusal(
                PluginRefusalCodes.CapabilityNotDeclared,
                "radio",
                "The plugin read the owner's library.",
                "It did not declare the library.read capability.",
                "Declare it in the manifest. Docs: /nomercy-plugins/capabilities/library-read",
                PluginRefusalSeverity.Blocked
            )
        );
}

internal sealed class RecordingLibrary : IPluginLibraryQuery
{
    public static readonly PluginEncodingPreset Streaming = new(
        "01KQK5NFSRXDP6F83NZK1YA6WF",
        "H.264 Streaming (Universal)",
        true,
        2160
    );

    public int Queries { get; private set; }

    public string? LastLibraryId { get; private set; }

    public int LastShowId { get; private set; }

    public Task<IReadOnlyList<PluginLibrary>> GetLibrariesAsync(CancellationToken ct = default)
    {
        Queries++;

        return Task.FromResult<IReadOnlyList<PluginLibrary>>([
            new PluginLibrary("films", "Films", "movie") { EncodingPresets = [Streaming] },
        ]);
    }

    public Task<IReadOnlyList<PluginLibraryShow>> GetShowsAsync(
        string? libraryId = null,
        CancellationToken ct = default
    )
    {
        Queries++;
        LastLibraryId = libraryId;

        return Task.FromResult<IReadOnlyList<PluginLibraryShow>>([
            new PluginLibraryShow(7, "The Ark", 2023, "tv", null, 36, 0)
            {
                AddedBy = PluginLibraryAddedBy.Manual,
            },
        ]);
    }

    public Task<IReadOnlyList<PluginLibraryMovie>> GetMoviesAsync(
        string? libraryId = null,
        CancellationToken ct = default
    )
    {
        Queries++;
        LastLibraryId = libraryId;

        return Task.FromResult<IReadOnlyList<PluginLibraryMovie>>([]);
    }

    public Task<IReadOnlyList<PluginLibraryEpisode>> GetEpisodesAsync(
        int showId,
        CancellationToken ct = default
    )
    {
        Queries++;
        LastShowId = showId;

        return Task.FromResult<IReadOnlyList<PluginLibraryEpisode>>([]);
    }

    public Task<IReadOnlyList<PluginLibraryFile>> GetShowFilesAsync(
        int showId,
        CancellationToken ct = default
    )
    {
        Queries++;
        LastShowId = showId;

        return Task.FromResult<IReadOnlyList<PluginLibraryFile>>([]);
    }
}
