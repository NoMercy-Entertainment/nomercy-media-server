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
using NoMercy.PluginHost;
using NoMercy.Plugins.Abstractions;
using NoMercy.Plugins.Ipc;
using Xunit;

namespace NoMercy.Tests.PluginHost;

/// <summary>
/// The plugin's folders, named by the server and opened by the plugin.
/// <para>
/// Bytes do not cross the channel, so what these check is that the right
/// folder is asked for, that the answer is opened locally, and that a folder
/// the owner never granted is refused rather than quietly empty.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public class RemoteStorageTests : IDisposable
{
    private static readonly Ulid PluginId = Ulid.NewUlid();

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"nomercy-storage-{Guid.NewGuid():n}"
    );

    public RemoteStorageTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task ThePluginWritesAndReadsItsOwnFolderWithoutTheBytesCrossing()
    {
        CountingBroker broker = Answering(_root);
        RemoteStorage storage = new(PluginId, new RemoteCall(PluginId, broker));

        await using (Stream write = await storage.Private.OpenWriteAsync("notes.txt", true))
        await using (StreamWriter writer = new(write))
            await writer.WriteAsync("kept");

        await using Stream read = await storage.Private.OpenReadAsync("notes.txt");
        using StreamReader reader = new(read);

        (await reader.ReadToEndAsync()).Should().Be("kept");

        // One call for the folder, and not one per byte.
        broker.Calls.Should().Be(1);
    }

    /// <summary>
    /// The plugin's own three folders cannot move for the life of the process,
    /// so asking again would make every read pay for the boundary for an
    /// answer that cannot have changed.
    /// </summary>
    [Fact]
    public void AFixedFolderIsAskedForOnceAndThenRemembered()
    {
        CountingBroker broker = Answering(_root);
        RemoteStorage storage = new(PluginId, new RemoteCall(PluginId, broker));

        _ = storage.Private;
        _ = storage.Private;
        _ = storage.Private;

        broker.Calls.Should().Be(1);
    }

    /// <summary>
    /// An absolute path and a <c>..</c> segment are the two ways out of a
    /// scope. The guard has to run on this side too: a machine whose platform
    /// has no sandbox would otherwise have nothing between a plugin and the
    /// server's own files.
    /// </summary>
    [Fact]
    public async Task APathThatClimbsOutOfTheFolderIsRefusedInThePluginsOwnProcess()
    {
        RemoteStorage storage = new(PluginId, new RemoteCall(PluginId, Answering(_root)));

        IPluginStorageScope scope = storage.Private;

        await Assert.ThrowsAsync<PluginRefusedException>(() =>
            scope.ExistsAsync("../../secrets.txt")
        );
    }

    [Fact]
    public async Task AnAbsolutePathIsRefusedInThePluginsOwnProcess()
    {
        RemoteStorage storage = new(PluginId, new RemoteCall(PluginId, Answering(_root)));

        IPluginStorageScope scope = storage.Private;

        await Assert.ThrowsAsync<PluginRefusedException>(() =>
            scope.ExistsAsync(Path.Combine(Path.GetTempPath(), "elsewhere.txt"))
        );
    }

    /// <summary>
    /// A folder the owner never granted is refused, and the refusal has to
    /// arrive as one. An empty scope would read to a plugin as a folder that
    /// exists and happens to be empty.
    /// </summary>
    [Fact]
    public async Task AFolderTheOwnerNeverGrantedIsRefusedRatherThanAnsweredEmpty()
    {
        CountingBroker broker = new(
            PluginCallResponse.Refused(
                new WireRefusal(
                    PluginRefusalCodes.FileOutsideGrant,
                    PluginId.ToString(),
                    "The plugin reached a path outside every folder it was granted: movies",
                    "The owner has not granted that folder.",
                    "Ask the owner for the folder. Docs: /nomercy-plugins/capabilities/storage",
                    PluginRefusalSeverity.Blocked.ToString()
                )
            )
        );

        RemoteStorage storage = new(PluginId, new RemoteCall(PluginId, broker));

        PluginRefusedException refused = await Assert.ThrowsAsync<PluginRefusedException>(() =>
            storage.PathAsync("movies")
        );

        refused.Refusal.Code.Should().Be(PluginRefusalCodes.FileOutsideGrant);
    }

    /// <summary>
    /// Per-user storage and the plugin's database do not cross yet, and a
    /// facade that answered an empty scope would look to a plugin exactly like
    /// a user who had never saved anything.
    /// </summary>
    [Fact]
    public void AMemberThatDoesNotCrossYetSaysSoRatherThanAnsweringEmpty()
    {
        RemoteStorage storage = new(PluginId, new RemoteCall(PluginId, Answering(_root)));

        Assert.Throws<PluginRefusedException>(() => storage.ForUser);
    }

    private static CountingBroker Answering(string root) =>
        new(PluginCallResponse.Value(JsonSerializer.Serialize(root)));
}
