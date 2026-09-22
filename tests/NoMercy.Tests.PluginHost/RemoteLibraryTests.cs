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
/// Reads of the owner's library, crossing as rows.
/// </summary>
[Trait("Category", "Unit")]
public class RemoteLibraryTests
{
    private static readonly Ulid PluginId = Ulid.NewUlid();

    [Fact]
    public async Task TheRowsCrossBackAsTheRecordsTheContractDeclares()
    {
        RemoteLibrary library = Build("""[{"id":"films","title":"Films","type":"movie"}]""");

        IReadOnlyList<PluginLibrary> libraries = await library.GetLibrariesAsync();

        libraries.Should().ContainSingle();
        libraries[0].Title.Should().Be("Films");
    }

    /// <summary>
    /// A library is what the owner is changing while the plugin runs, so an
    /// answer remembered from startup is a plugin that cannot see the show its
    /// owner just added.
    /// </summary>
    [Fact]
    public async Task NothingIsRememberedBecauseTheOwnerKeepsChangingTheLibrary()
    {
        CountingBroker broker = new(PluginCallResponse.Value("[]"));
        RemoteLibrary library = new(PluginId, new RemoteCall(PluginId, broker));

        await library.GetLibrariesAsync();
        await library.GetLibrariesAsync();

        broker.Calls.Should().Be(2);
    }

    /// <summary>
    /// An empty answer is a library with nothing in it, and a null would be a
    /// plugin crashing on the owner's first empty folder.
    /// </summary>
    [Fact]
    public async Task AnEmptyAnswerIsAnEmptyListRatherThanNothingAtAll()
    {
        RemoteLibrary library = Build("null");

        (await library.GetMoviesAsync()).Should().BeEmpty();
    }

    /// <summary>
    /// Offering a file to a library, and watching one, do not cross yet. A
    /// watch that never fired would look to a plugin like a library nobody is
    /// touching.
    /// </summary>
    [Fact]
    public void AMemberThatDoesNotCrossYetSaysSoRatherThanAnsweringEmpty()
    {
        RemoteLibrary library = Build("[]");

        Assert.Throws<PluginRefusedException>(() => library.Import);
        Assert.Throws<PluginRefusedException>(() => library.Watch);
    }

    private static RemoteLibrary Build(string payloadJson) =>
        new(
            PluginId,
            new RemoteCall(PluginId, new CountingBroker(PluginCallResponse.Value(payloadJson)))
        );
}
