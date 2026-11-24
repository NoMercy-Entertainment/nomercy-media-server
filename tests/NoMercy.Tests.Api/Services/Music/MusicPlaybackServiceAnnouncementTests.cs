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

using System.Collections.Concurrent;
using FluentAssertions;
using Moq;
using NoMercy.Api.DTOs.Music;
using NoMercy.Api.Services.Music;
using NoMercy.Database.Models.Music;
using NoMercy.Database.Models.Users;
using NoMercy.Events;
using NoMercy.Events.Plugins;
using NoMercy.Networking.Messaging;
using NoMercy.PluginSdk.Abstractions;
using Xunit;

namespace NoMercy.Tests.Api.Services.Music;

/// <summary>
/// Every state the music player broadcasts is also what plugins are told
/// about, whichever part of the player changed it.
/// </summary>
[Trait("Category", "Unit")]
public sealed class MusicPlaybackServiceAnnouncementTests : IDisposable
{
    private readonly InMemoryEventBus _bus = new();
    private readonly MusicPlaybackAnnouncer _announcer;
    private readonly MusicPlaybackService _service;
    private readonly Mock<IClientMessenger> _messenger = new();
    private readonly ConcurrentQueue<string> _topics = new();
    private readonly SemaphoreSlim _published = new(0);
    private readonly IDisposable _subscription;
    private readonly User _user = new() { Id = Guid.NewGuid(), Name = "listener" };

    public MusicPlaybackServiceAnnouncementTests()
    {
        _messenger
            .Setup(messenger =>
                messenger.SendTo(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<Guid>(),
                    It.IsAny<object?>()
                )
            )
            .Returns(Task.CompletedTask);
        _announcer = new(_bus);
        _service = new(
            new(),
            Mock.Of<IServiceProvider>(),
            _messenger.Object,
            new(),
            announcer: _announcer
        );
        _subscription = _bus.Subscribe<PluginMessageEvent>(
            (message, _) =>
            {
                _topics.Enqueue(message.Name);
                _published.Release();
                return Task.CompletedTask;
            }
        );
    }

    public void Dispose()
    {
        _subscription.Dispose();
        _announcer.Dispose();
    }

    [Fact]
    public async Task A_broadcast_state_with_a_track_is_a_start_for_plugins()
    {
        await _service.UpdatePlaybackState(_user, Playing());

        (await Heard(1)).Should().Equal(PluginTopics.MusicPlaybackStarted);
    }

    /// <summary>
    /// A broadcast that repeats the one before it within the coalescing
    /// window is not sent to the clients again. What changed in it for a
    /// plugin is still said.
    /// </summary>
    [Fact]
    public async Task A_change_in_a_broadcast_that_is_coalesced_is_still_announced()
    {
        MusicPlayerState state = Playing();
        await _service.UpdatePlaybackState(_user, state);

        state.Shuffle = true;
        await _service.UpdatePlaybackState(_user, state);

        (await Heard(2))
            .Should()
            .Equal(PluginTopics.MusicPlaybackStarted, PluginTopics.MusicPlaybackModeChanged);
    }

    [Fact]
    public async Task A_state_without_a_listener_session_is_a_stop()
    {
        await _service.UpdatePlaybackState(_user, Playing());

        await _service.UpdatePlaybackState(_user, null);

        (await Heard(2))
            .Should()
            .Equal(PluginTopics.MusicPlaybackStarted, PluginTopics.MusicPlaybackStopped);
    }

    /// <summary>A listener who disconnected for good leaves nothing behind to compare with.</summary>
    [Fact]
    public async Task A_listener_who_left_and_returns_starts_again()
    {
        MusicPlayerState state = Playing();
        await _service.UpdatePlaybackState(_user, state);

        _service.RemoveDisconnectedUserState(_user.Id);
        await _service.UpdatePlaybackState(_user, state);

        (await Heard(2))
            .Should()
            .Equal(PluginTopics.MusicPlaybackStarted, PluginTopics.MusicPlaybackStarted);
    }

    /// <summary>A server without the announcer broadcasts exactly as before.</summary>
    [Fact]
    public async Task Without_an_announcer_the_state_is_still_broadcast()
    {
        MusicPlaybackService plain = new(
            new(),
            Mock.Of<IServiceProvider>(),
            _messenger.Object,
            new()
        );

        await plain.UpdatePlaybackState(_user, Playing());

        _messenger.Verify(
            messenger =>
                messenger.SendTo("MusicPlayerState", "musicHub", _user.Id, It.IsAny<object?>()),
            Times.Once
        );
        _topics.Should().BeEmpty();
    }

    private async Task<string[]> Heard(int count)
    {
        for (int taken = 0; taken < count; taken++)
            (await _published.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeTrue();

        return [.. _topics];
    }

    private static MusicPlayerState Playing() =>
        new()
        {
            CurrentItem = Track("playing"),
            Playlist = [Track("next")],
            PlayState = true,
            DeviceId = "phone",
        };

    private static PlaylistTrackDto Track(string name) =>
        new(
            new Track
            {
                Id = Guid.NewGuid(),
                Name = name,
                Duration = "180",
                Filename = $"{name}.mp3",
                Folder = "/music/",
                FolderId = Ulid.NewUlid(),
            },
            "US"
        );
}
