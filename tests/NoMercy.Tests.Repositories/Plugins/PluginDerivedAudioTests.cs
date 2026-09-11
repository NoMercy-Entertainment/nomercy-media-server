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
using Moq;
using NoMercy.Data.Plugins;
using NoMercy.MediaProcessing.DerivedAudio;
using NoMercy.Plugins.Abstractions;

namespace NoMercy.Tests.Repositories.Plugins;

/// <summary>
/// The host side of <see cref="IPluginDerivedAudio" />: every member forwards
/// to <see cref="IDerivedAudioStore" />, and an empty key is refused before it
/// ever reaches the store, since <see cref="IDerivedAudioStore.RelativePath" />
/// slices the key to build a path.
/// </summary>
public class PluginDerivedAudioTests
{
    [Fact]
    public async Task Put_ForwardsAndReturnsTheKey()
    {
        Mock<IDerivedAudioStore> store = new();
        using MemoryStream content = new([1, 2, 3]);
        DerivedAudioEntry entry = new("abcdef1234567890", "audio/opus", 3);

        store
            .Setup(s => s.PutAsync(content, "audio/opus", It.IsAny<CancellationToken>()))
            .ReturnsAsync(entry);

        PluginDerivedAudio facade = new(store.Object);

        string key = await facade.PutAsync(content, "audio/opus");

        key.Should().Be("abcdef1234567890");
        store.Verify(
            s => s.PutAsync(content, "audio/opus", It.IsAny<CancellationToken>()),
            Times.Once
        );
    }

    [Fact]
    public async Task OpenRead_ForwardsNullForUnknownKeys()
    {
        Mock<IDerivedAudioStore> store = new();
        store
            .Setup(s => s.OpenReadAsync("unknown-key", It.IsAny<CancellationToken>()))
            .ReturnsAsync((Stream?)null);

        PluginDerivedAudio facade = new(store.Object);

        Stream? result = await facade.OpenReadAsync("unknown-key");

        result.Should().BeNull();
        store.Verify(
            s => s.OpenReadAsync("unknown-key", It.IsAny<CancellationToken>()),
            Times.Once
        );
    }

    [Fact]
    public async Task Delete_Forwards()
    {
        Mock<IDerivedAudioStore> store = new();
        store
            .Setup(s => s.DeleteAsync("some-key", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        PluginDerivedAudio facade = new(store.Object);

        await facade.DeleteAsync("some-key");

        store.Verify(s => s.DeleteAsync("some-key", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Touch_Forwards()
    {
        Mock<IDerivedAudioStore> store = new();
        store
            .Setup(s => s.TouchAsync("some-key", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        PluginDerivedAudio facade = new(store.Object);

        await facade.TouchAsync("some-key");

        store.Verify(s => s.TouchAsync("some-key", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Exists_Forwards()
    {
        Mock<IDerivedAudioStore> store = new();
        store
            .Setup(s => s.ExistsAsync("some-key", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        PluginDerivedAudio facade = new(store.Object);

        bool exists = await facade.ExistsAsync("some-key");

        exists.Should().BeTrue();
        store.Verify(s => s.ExistsAsync("some-key", It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// <see cref="IDerivedAudioStore.RelativePath" /> slices the key to build a
    /// path, so a null or empty key must never reach the store at all - the
    /// facade answers "not found" / no-op on its own.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task AnEmptyKey_NeverReachesTheStore(string? key)
    {
        Mock<IDerivedAudioStore> store = new();
        PluginDerivedAudio facade = new(store.Object);

        bool exists = await facade.ExistsAsync(key!);
        Stream? opened = await facade.OpenReadAsync(key!);
        await facade.TouchAsync(key!);
        await facade.DeleteAsync(key!);

        exists.Should().BeFalse();
        opened.Should().BeNull();

        store.Verify(
            s => s.ExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never
        );
        store.Verify(
            s => s.OpenReadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never
        );
        store.Verify(
            s => s.TouchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never
        );
        store.Verify(
            s => s.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never
        );
    }
}
