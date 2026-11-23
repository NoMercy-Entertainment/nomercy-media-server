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
using NoMercy.Api.DTOs.Music;
using NoMercy.Api.Services.Music;
using NoMercy.Database.Models.Music;
using NoMercy.Events;
using NoMercy.Events.Plugins;
using NoMercy.PluginSdk.Abstractions;
using Xunit;

namespace NoMercy.Tests.Api.Services.Music;

/// <summary>
/// What plugins are told about a listener's playback: one announcement per
/// thing that changed between two states the player broadcasts.
/// </summary>
[Trait("Category", "Unit")]
public sealed class MusicPlaybackAnnouncerTests : IDisposable
{
    private const long Now = 1_800_000_000_000;

    private static readonly Guid Listener = Guid.NewGuid();

    private readonly InMemoryEventBus _bus = new();
    private readonly MusicPlaybackAnnouncer _announcer;

    public MusicPlaybackAnnouncerTests()
    {
        _announcer = new(_bus);
    }

    public void Dispose() => _announcer.Dispose();

    [Fact]
    public void A_track_that_starts_is_announced_with_its_queue_and_modes()
    {
        MusicPlayerState state = Playing(out PlaylistTrackDto track, out PlaylistTrackDto next);
        state.Repeat = "all";
        state.Shuffle = true;
        state.DeviceId = "tv";
        state.CurrentList = new("/music/album/abc", UriKind.Relative);
        state.Time = 1200;

        MusicPlaybackAnnouncer.Announcement said = Changes(state).Should().ContainSingle().Subject;

        said.Topic.Should().Be(PluginTopics.MusicPlaybackStarted);
        said.Payload.Should()
            .BeEquivalentTo(
                new PluginMusicPlaybackStarted(
                    Listener,
                    track.Id,
                    "tv",
                    1200,
                    [next.Id],
                    "all",
                    true
                )
                {
                    CurrentList = "/music/album/abc",
                }
            );
    }

    [Fact]
    public void The_same_state_again_announces_nothing()
    {
        MusicPlayerState state = Playing(out _, out _);
        Changes(state);

        Changes(state).Should().BeEmpty();
    }

    /// <summary>The step to the next track is a start; its queue travels with it.</summary>
    [Fact]
    public void The_next_track_is_a_start_and_not_a_change_of_the_queue()
    {
        MusicPlayerState state = Playing(out _, out PlaylistTrackDto next);
        Changes(state);

        state.CurrentItem = next;
        state.Playlist = [];
        state.Time = 0;

        Changes(state).Select(said => said.Topic).Should().Equal(PluginTopics.MusicPlaybackStarted);
    }

    [Fact]
    public void A_pause_and_a_resume_are_announced_with_the_position()
    {
        MusicPlayerState state = Playing(out PlaylistTrackDto track, out _);
        Changes(state);

        state.PlayState = false;
        state.Time = 4000;
        MusicPlaybackAnnouncer.Announcement paused = Changes(state, Now + 4000)
            .Should()
            .ContainSingle()
            .Subject;

        state.PlayState = true;
        MusicPlaybackAnnouncer.Announcement resumed = Changes(state, Now + 60_000)
            .Should()
            .ContainSingle()
            .Subject;

        paused.Topic.Should().Be(PluginTopics.MusicPlaybackPaused);
        paused.Payload.Should().Be(new PluginMusicPlaybackPosition(Listener, track.Id, 4000));
        resumed.Topic.Should().Be(PluginTopics.MusicPlaybackResumed);
        resumed.Payload.Should().Be(new PluginMusicPlaybackPosition(Listener, track.Id, 4000));
    }

    [Fact]
    public void A_jump_in_the_position_is_a_seek()
    {
        MusicPlayerState state = Playing(out PlaylistTrackDto track, out _);
        Changes(state);

        state.Time = 90_000;

        MusicPlaybackAnnouncer.Announcement said = Changes(state, Now + 1000)
            .Should()
            .ContainSingle()
            .Subject;
        said.Topic.Should().Be(PluginTopics.MusicPlaybackSeeked);
        said.Payload.Should().Be(new PluginMusicPlaybackPosition(Listener, track.Id, 90_000));
    }

    /// <summary>The track playing on is not a jump, and neither is a player's small correction.</summary>
    [Theory]
    [InlineData(5000, 5000)]
    [InlineData(5000, 4700)]
    [InlineData(5000, 6900)]
    public void Playing_on_is_not_a_seek(int elapsedMs, int positionMs)
    {
        MusicPlayerState state = Playing(out _, out _);
        Changes(state);

        state.Time = positionMs;

        Changes(state, Now + elapsedMs).Should().BeEmpty();
    }

    /// <summary>A paused track stands still, so time that passes is not expected to move it.</summary>
    [Fact]
    public void Time_that_passes_while_paused_is_not_a_seek()
    {
        MusicPlayerState state = Playing(out _, out _);
        state.PlayState = false;
        Changes(state);

        Changes(state, Now + 600_000).Should().BeEmpty();
    }

    [Fact]
    public void A_seek_while_paused_is_a_seek()
    {
        MusicPlayerState state = Playing(out _, out _);
        state.PlayState = false;
        Changes(state);

        state.Time = 30_000;

        Changes(state, Now + 1000)
            .Select(said => said.Topic)
            .Should()
            .Equal(PluginTopics.MusicPlaybackSeeked);
    }

    [Fact]
    public void Playback_that_ends_is_announced_once()
    {
        MusicPlayerState state = Playing(out _, out _);
        Changes(state);

        state.CurrentItem = null;
        MusicPlaybackAnnouncer.Announcement said = Changes(state).Should().ContainSingle().Subject;

        said.Topic.Should().Be(PluginTopics.MusicPlaybackStopped);
        said.Payload.Should().Be(new PluginMusicPlaybackStopped(Listener));
        Changes(state).Should().BeEmpty();
        Changes(null).Should().BeEmpty();
    }

    [Fact]
    public void A_listener_whose_state_is_gone_has_stopped()
    {
        Changes(Playing(out _, out _));

        Changes(null).Select(said => said.Topic).Should().Equal(PluginTopics.MusicPlaybackStopped);
    }

    [Fact]
    public void A_listener_who_never_played_is_not_announced_as_stopped()
    {
        Changes(new()).Should().BeEmpty();
        Changes(null).Should().BeEmpty();
    }

    [Fact]
    public void A_queue_in_another_order_is_announced()
    {
        MusicPlayerState state = Playing(out PlaylistTrackDto track, out PlaylistTrackDto next);
        PlaylistTrackDto later = Track("later");
        state.Playlist = [next, later];
        Changes(state);

        state.Playlist = [later, next];

        MusicPlaybackAnnouncer.Announcement said = Changes(state).Should().ContainSingle().Subject;
        said.Topic.Should().Be(PluginTopics.MusicPlaybackQueueChanged);
        said.Payload.Should()
            .BeEquivalentTo(
                new PluginMusicPlaybackQueueChanged(Listener, track.Id, [later.Id, next.Id])
            );
    }

    [Fact]
    public void Another_device_is_announced()
    {
        MusicPlayerState state = Playing(out _, out _);
        state.DeviceId = "phone";
        Changes(state);

        state.DeviceId = "tv";

        MusicPlaybackAnnouncer.Announcement said = Changes(state).Should().ContainSingle().Subject;
        said.Topic.Should().Be(PluginTopics.MusicPlaybackDeviceChanged);
        said.Payload.Should().Be(new PluginMusicPlaybackDeviceChanged(Listener, "tv"));
    }

    /// <summary>Device ids are compared without case everywhere in the music hub.</summary>
    [Fact]
    public void The_same_device_in_other_casing_is_not_another_device()
    {
        MusicPlayerState state = Playing(out _, out _);
        state.DeviceId = "Phone-1";
        Changes(state);

        state.DeviceId = "phone-1";

        Changes(state).Should().BeEmpty();
    }

    [Fact]
    public void A_session_nobody_holds_any_more_is_a_change_of_device_to_none()
    {
        MusicPlayerState state = Playing(out _, out _);
        state.DeviceId = "phone";
        Changes(state);

        state.DeviceId = null;

        Changes(state)
            .Should()
            .ContainSingle()
            .Which.Payload.Should()
            .Be(new PluginMusicPlaybackDeviceChanged(Listener, null));
    }

    [Theory]
    [InlineData("one", false)]
    [InlineData("off", true)]
    public void Repeat_or_shuffle_that_changes_is_announced(string repeat, bool shuffle)
    {
        MusicPlayerState state = Playing(out PlaylistTrackDto track, out _);
        Changes(state);

        state.Repeat = repeat;
        state.Shuffle = shuffle;

        MusicPlaybackAnnouncer.Announcement said = Changes(state).Should().ContainSingle().Subject;
        said.Topic.Should().Be(PluginTopics.MusicPlaybackModeChanged);
        said.Payload.Should()
            .Be(new PluginMusicPlaybackModeChanged(Listener, track.Id, repeat, shuffle));
    }

    /// <summary>Repeat one plays the same track again; for a plugin that is a track that starts.</summary>
    [Fact]
    public void The_same_track_again_after_its_end_is_a_start()
    {
        MusicPlayerState state = Playing(out _, out _);
        state.Time = 170_000;
        Changes(state);

        _announcer.TrackEnded(Listener);
        state.Time = 0;

        Changes(state).Select(said => said.Topic).Should().Equal(PluginTopics.MusicPlaybackStarted);
        Changes(state).Should().BeEmpty();
    }

    [Fact]
    public void A_track_that_arrives_paused_is_a_start_and_a_pause()
    {
        MusicPlayerState state = Playing(out _, out _);
        state.PlayState = false;

        Changes(state)
            .Select(said => said.Topic)
            .Should()
            .Equal(PluginTopics.MusicPlaybackStarted, PluginTopics.MusicPlaybackPaused);
    }

    [Fact]
    public void A_forgotten_listener_is_not_announced_as_stopped()
    {
        Changes(Playing(out _, out _));

        _announcer.Forget(Listener);

        Changes(null).Should().BeEmpty();
    }

    [Fact]
    public void Each_listener_is_followed_on_its_own()
    {
        Guid other = Guid.NewGuid();
        MusicPlayerState state = Playing(out _, out _);
        Changes(state);

        _announcer
            .Changes(other, state, Now)
            .Select(said => said.Topic)
            .Should()
            .Equal(PluginTopics.MusicPlaybackStarted);
    }

    /// <summary>
    /// What reaches the bus is the host's own word (sender empty), with the
    /// field names the SDK documents.
    /// </summary>
    [Fact]
    public async Task What_is_observed_is_published_as_a_host_topic_in_order()
    {
        List<PluginMessageEvent> published = [];
        TaskCompletionSource both = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using IDisposable subscription = _bus.Subscribe<PluginMessageEvent>(
            (message, _) =>
            {
                published.Add(message);
                if (published.Count == 2)
                    both.TrySetResult();
                return Task.CompletedTask;
            }
        );
        MusicPlayerState state = Playing(out PlaylistTrackDto track, out PlaylistTrackDto next);

        _announcer.Observe(Listener, state);
        _announcer.Observe(Listener, null);
        await both.Task.WaitAsync(TimeSpan.FromSeconds(10));

        published
            .Select(message => message.Name)
            .Should()
            .Equal(PluginTopics.MusicPlaybackStarted, PluginTopics.MusicPlaybackStopped);
        published.Should().OnlyContain(message => message.PluginId == Ulid.Empty);

        JsonElement started = JsonSerializer.SerializeToElement(published[0].Payload);
        started.GetProperty("userId").GetGuid().Should().Be(Listener);
        started.GetProperty("trackId").GetGuid().Should().Be(track.Id);
        started.GetProperty("positionMs").GetInt32().Should().Be(0);
        started.GetProperty("queue")[0].GetGuid().Should().Be(next.Id);
        started.GetProperty("repeat").GetString().Should().Be("off");
        started.GetProperty("shuffle").GetBoolean().Should().BeFalse();
    }

    /// <summary>A subscriber that throws must not end the announcements after it.</summary>
    [Fact]
    public async Task A_handler_that_fails_does_not_stop_what_follows()
    {
        int seen = 0;
        TaskCompletionSource second = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using IDisposable subscription = _bus.Subscribe<PluginMessageEvent>(
            (_, _) =>
            {
                if (Interlocked.Increment(ref seen) == 1)
                    throw new InvalidOperationException("a plugin's handler broke");

                second.TrySetResult();
                return Task.CompletedTask;
            }
        );

        _announcer.Observe(Listener, Playing(out _, out _));
        _announcer.Observe(Listener, null);

        await second.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private IReadOnlyList<MusicPlaybackAnnouncer.Announcement> Changes(
        MusicPlayerState? state,
        long nowMs = Now
    ) => _announcer.Changes(Listener, state, nowMs);

    private static MusicPlayerState Playing(out PlaylistTrackDto track, out PlaylistTrackDto next)
    {
        track = Track("playing");
        next = Track("next");

        return new()
        {
            CurrentItem = track,
            Playlist = [next],
            PlayState = true,
        };
    }

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
