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
using Microsoft.Extensions.Logging;
using Moq;
using NoMercy.Data.Plugins;
using NoMercy.MediaProcessing.DerivedAudio;
using NoMercy.Plugins.Abstractions;

namespace NoMercy.Tests.Repositories.Plugins;

/// <summary>
/// The host side of <see cref="IPluginDerivedAudio" />: every member forwards
/// to <see cref="IDerivedAudioStore" />, which is the only key validator -
/// a key it could not have minted comes back as an absence from there, not
/// from a second copy of the rule in the facade.
/// </summary>
public class PluginDerivedAudioTests
{
    // A 64-character lowercase hex string: the shape PutAsync mints, which is
    // what the forwarding tests below hand the store.
    private const string SomeKey =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private const string UnknownKey =
        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

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
            .Setup(s => s.OpenReadAsync(UnknownKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Stream?)null);

        PluginDerivedAudio facade = new(store.Object);

        Stream? result = await facade.OpenReadAsync(UnknownKey);

        result.Should().BeNull();
        store.Verify(s => s.OpenReadAsync(UnknownKey, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Delete_Forwards()
    {
        Mock<IDerivedAudioStore> store = new();
        store
            .Setup(s => s.DeleteAsync(SomeKey, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        PluginDerivedAudio facade = new(store.Object);

        await facade.DeleteAsync(SomeKey);

        store.Verify(s => s.DeleteAsync(SomeKey, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Touch_Forwards()
    {
        Mock<IDerivedAudioStore> store = new();
        store.Setup(s => s.TouchAsync(SomeKey, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        PluginDerivedAudio facade = new(store.Object);

        await facade.TouchAsync(SomeKey);

        store.Verify(s => s.TouchAsync(SomeKey, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Exists_Forwards()
    {
        Mock<IDerivedAudioStore> store = new();
        store.Setup(s => s.ExistsAsync(SomeKey, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        PluginDerivedAudio facade = new(store.Object);

        bool exists = await facade.ExistsAsync(SomeKey);

        exists.Should().BeTrue();
        store.Verify(s => s.ExistsAsync(SomeKey, It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// The store is the only key validator - it answers "not found" for a key
    /// <see cref="IDerivedAudioStore.PutAsync" /> could not have minted and
    /// never builds a path out of one. The facade forwards and hands back
    /// whatever the store said, rather than keeping a second copy of the rule
    /// that would have to be kept in step with it.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task AnEmptyKey_IsForwardedToTheStore(string? key)
    {
        Mock<IDerivedAudioStore> store = new();
        store
            .Setup(s => s.ExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        store
            .Setup(s => s.OpenReadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Stream?)null);

        PluginDerivedAudio facade = new(store.Object);

        bool exists = await facade.ExistsAsync(key!);
        Stream? opened = await facade.OpenReadAsync(key!);
        await facade.TouchAsync(key!);
        await facade.DeleteAsync(key!);

        exists.Should().BeFalse();
        opened.Should().BeNull();

        store.Verify(s => s.ExistsAsync(key!, It.IsAny<CancellationToken>()), Times.Once);
        store.Verify(s => s.OpenReadAsync(key!, It.IsAny<CancellationToken>()), Times.Once);
        store.Verify(s => s.TouchAsync(key!, It.IsAny<CancellationToken>()), Times.Once);
        store.Verify(s => s.DeleteAsync(key!, It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// None of these members has a way to say why it could not answer, so a
    /// store that throws reads as an absence rather than taking the plugin's
    /// sweep down with it. <see cref="PluginDerivedAudio.PutAsync" /> is the
    /// exception: it owes the caller the key of content it just produced, and
    /// there is none.
    /// </summary>
    [Fact]
    public async Task AThrowingStore_ReadsAsAnAbsence_ExceptOnPut()
    {
        Mock<IDerivedAudioStore> store = new();
        IOException failure = new("the derived volume went away");
        store
            .Setup(s => s.ExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(failure);
        store
            .Setup(s => s.OpenReadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(failure);
        store
            .Setup(s => s.TouchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(failure);
        store
            .Setup(s => s.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(failure);
        store
            .Setup(s =>
                s.PutAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>())
            )
            .ThrowsAsync(failure);

        PluginDerivedAudio facade = new(store.Object);

        (await facade.ExistsAsync(SomeKey)).Should().BeFalse();
        (await facade.OpenReadAsync(SomeKey)).Should().BeNull();

        Func<Task> touch = () => facade.TouchAsync(SomeKey);
        await touch.Should().NotThrowAsync();
        Func<Task> delete = () => facade.DeleteAsync(SomeKey);
        await delete.Should().NotThrowAsync();

        using MemoryStream content = new([1, 2, 3]);
        Func<Task> put = () => facade.PutAsync(content, "audio/opus");
        await put.Should().ThrowAsync<IOException>();
    }

    /// <summary>
    /// A key is the lowercase hex of a SHA-256 digest and nothing else, so a
    /// short one, a traversal attempt, a near-miss length and the right
    /// characters in the wrong case all come back as an absence - decided by
    /// the store, which is where that rule lives, and forwarded unchanged.
    /// <c>DerivedAudioStoreTests.AnInvalidKey_IsNotFound_AndNeverTouchesTheDisk</c>
    /// is what proves none of them ever becomes a path.
    /// </summary>
    [Theory]
    [InlineData("a")]
    [InlineData("../../etc")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task AMalformedKey_IsForwardedAndReadsAsAnAbsence(string key)
    {
        Mock<IDerivedAudioStore> store = new();
        store
            .Setup(s => s.ExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        store
            .Setup(s => s.OpenReadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Stream?)null);

        PluginDerivedAudio facade = new(store.Object);

        bool exists = await facade.ExistsAsync(key);
        Stream? opened = await facade.OpenReadAsync(key);
        await facade.TouchAsync(key);
        await facade.DeleteAsync(key);

        exists.Should().BeFalse();
        opened.Should().BeNull();

        store.Verify(s => s.ExistsAsync(key, It.IsAny<CancellationToken>()), Times.Once);
        store.Verify(s => s.OpenReadAsync(key, It.IsAny<CancellationToken>()), Times.Once);
        store.Verify(s => s.TouchAsync(key, It.IsAny<CancellationToken>()), Times.Once);
        store.Verify(s => s.DeleteAsync(key, It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// One facade serves every plugin, so there is no plugin of its own to
    /// name - and it names the empty ULID rather than a word, because
    /// <c>PluginId</c> is a ULID string in every entry the guard writes and a
    /// consumer should be able to parse it without a special case.
    /// <para>
    /// Asserted on the put, which is the one member that logs by hand: its
    /// former distinctive sentence is the guard's template now, so this pins
    /// both halves of that.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AFailedPut_LogsTheSharedFacadeAsTheEmptyUlid()
    {
        Mock<IDerivedAudioStore> store = new();
        store
            .Setup(s =>
                s.PutAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>())
            )
            .ThrowsAsync(new IOException("the derived volume went away"));

        List<KeyValuePair<string, object?>> logged = [];
        Mock<ILogger<PluginDerivedAudio>> logger = new();
        logger
            .Setup(log =>
                log.Log(
                    LogLevel.Warning,
                    It.IsAny<EventId>(),
                    It.IsAny<It.IsAnyType>(),
                    It.IsAny<Exception?>(),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()
                )
            )
            .Callback(
                new InvocationAction(invocation =>
                    logged.AddRange(
                        (IReadOnlyList<KeyValuePair<string, object?>>)invocation.Arguments[2]
                    )
                )
            );

        PluginDerivedAudio facade = new(store.Object, logger.Object);

        using MemoryStream content = new([1, 2, 3]);
        Func<Task> put = () => facade.PutAsync(content, "audio/opus");
        await put.Should().ThrowAsync<IOException>();

        logged
            .Should()
            .Contain(entry =>
                entry.Key == "{OriginalFormat}"
                && (string)entry.Value! == "plugin {PluginId}: {Member} failed inside the server"
            );
        logged
            .Should()
            .Contain(entry =>
                entry.Key == "PluginId" && (string)entry.Value! == Ulid.Empty.ToString()
            );
    }
}
