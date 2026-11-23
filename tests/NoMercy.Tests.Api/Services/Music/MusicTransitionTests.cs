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
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NoMercy.Api.DTOs.Music;
using NoMercy.Api.Hubs;
using NoMercy.Api.Services.Music;
using NoMercy.Data.Repositories;
using NoMercy.Database.Models.Music;
using NoMercy.Database.Models.Users;
using NoMercy.Networking.Messaging;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Access;
using NoMercy.PluginSdk.Media;
using Xunit;

namespace NoMercy.Tests.Api.Services.Music;

/// <summary>
/// Audio a plugin offers for the join out of the playing track: what the
/// player answers, when the listener's players are sent it, and when the
/// offer ends because it no longer fits what plays.
/// </summary>
[Trait("Category", "Unit")]
public sealed class MusicTransitionTests : IDisposable
{
    private const string Device = "phone";
    private const int StopMs = 170_000;
    private const int ResumeMs = 12_000;

    private static readonly Ulid PluginId = Ulid.Parse("01ARZ3NDEKTSV4RRFFQ69G5FAV");

    private readonly User _user = new() { Id = Guid.NewGuid(), Name = "listener" };
    private readonly MusicPlayerStateManager _states = new();
    private readonly List<(string Name, object? Payload)> _sent = [];
    private readonly ServiceProvider _services;
    private readonly PluginMediaTicketMinter _minter = new(TimeProvider.System, new byte[32]);
    private readonly Mock<IPluginAccessResolver> _access = new();
    private readonly MusicPlaybackService _playback;
    private readonly PluginMusicPlayerHost _host;
    private readonly MusicPlaybackCommandHandler _commands;

    private readonly PlaylistTrackDto _playing = Track("playing");
    private readonly PlaylistTrackDto _next = Track("next");
    private readonly PlaylistTrackDto _later = Track("later");

    public MusicTransitionTests()
    {
        Mock<IClientMessenger> messenger = new();
        messenger
            .Setup(clients =>
                clients.SendTo(It.IsAny<string>(), "musicHub", _user.Id, It.IsAny<object?>())
            )
            .Callback(
                (string name, string _, Guid _, object? payload) => _sent.Add((name, payload))
            )
            .Returns(Task.CompletedTask);

        Mock<IUserRepository> users = new();
        users.Setup(repository => repository.GetByIdAsync(_user.Id)).ReturnsAsync(_user);
        _access.Setup(access => access.Resolve(PluginId, _user.Id)).Returns(PluginAccess.Shared);

        _services = new ServiceCollection().AddSingleton(users.Object).BuildServiceProvider();
        _playback = new(
            _states,
            _services,
            messenger.Object,
            new(),
            transitionLinks: new(_minter, _access.Object)
        );
        _host = new(_states, _playback, _services.GetRequiredService<IServiceScopeFactory>());
        _commands = new(_playback);
    }

    public void Dispose()
    {
        _playback.RemoveTimer(_user.Id);
        _services.Dispose();
    }

    // ── What the player answers an offer ─────────────────────────────────────

    [Fact]
    public async Task An_offer_for_the_playing_track_and_the_next_one_is_kept()
    {
        MusicPlayerState state = Play();

        PluginMusicTransitionAnswer answer = await OfferAsync(Join());

        answer.Should().Be(PluginMusicTransitionAnswer.Accepted);
        state.Transition.Should().NotBeNull();
        state.Transition!.PluginId.Should().Be(PluginId);
        state.Transition.Sent.Should().BeFalse();
        _sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Nothing_is_offered_to_a_listener_who_plays_nothing()
    {
        (await OfferAsync(Join())).Should().Be(PluginMusicTransitionAnswer.NothingPlaying);

        _states.UpdateState(_user.Id, new());

        (await OfferAsync(Join())).Should().Be(PluginMusicTransitionAnswer.NothingPlaying);
    }

    [Fact]
    public async Task An_offer_out_of_another_track_than_the_playing_one_is_not_kept()
    {
        MusicPlayerState state = Play();

        PluginMusicTransitionAnswer answer = await OfferAsync(
            Join() with
            {
                FromTrackId = _later.Id,
            }
        );

        answer.Should().Be(PluginMusicTransitionAnswer.NotThePlayingTrack);
        state.Transition.Should().BeNull();
    }

    [Fact]
    public async Task An_offer_into_another_track_than_the_next_one_is_not_kept()
    {
        MusicPlayerState state = Play();

        PluginMusicTransitionAnswer answer = await OfferAsync(
            Join() with
            {
                ToTrackId = _later.Id,
            }
        );

        answer.Should().Be(PluginMusicTransitionAnswer.NotTheNextTrack);
        state.Transition.Should().BeNull();
    }

    /// <summary>With repeat one the same track plays again: there is no next track to lead into.</summary>
    [Fact]
    public async Task With_repeat_one_there_is_no_next_track_to_lead_into()
    {
        Play().Repeat = "one";

        (await OfferAsync(Join())).Should().Be(PluginMusicTransitionAnswer.NotTheNextTrack);
    }

    [Theory]
    [InlineData(180_001, ResumeMs)]
    [InlineData(StopMs, 180_001)]
    public async Task A_stop_or_a_resume_past_the_end_of_its_track_is_not_kept(
        int outgoingStopMs,
        int incomingResumeMs
    )
    {
        Play();

        PluginMusicTransitionAnswer answer = await OfferAsync(
            Join() with
            {
                OutgoingStopMs = outgoingStopMs,
                IncomingResumeMs = incomingResumeMs,
            }
        );

        answer.Should().Be(PluginMusicTransitionAnswer.OutsideTheTrack);
    }

    [Fact]
    public async Task An_offer_whose_stop_is_already_behind_is_too_late()
    {
        Play().Time = StopMs;

        (await OfferAsync(Join())).Should().Be(PluginMusicTransitionAnswer.TooLate);
    }

    [Fact]
    public async Task An_offer_after_the_crossfade_signal_or_during_a_fade_is_too_late()
    {
        MusicPlayerState state = Play();
        state.CrossfadeSignalSent = true;

        (await OfferAsync(Join())).Should().Be(PluginMusicTransitionAnswer.TooLate);

        state.CrossfadeSignalSent = false;
        _playback.StartCrossfade(_user.Id, Device, 3000);

        (await OfferAsync(Join())).Should().Be(PluginMusicTransitionAnswer.TooLate);
    }

    [Fact]
    public async Task A_new_offer_replaces_the_one_before()
    {
        MusicPlayerState state = Play();
        await OfferAsync(Join());

        await OfferAsync(Join() with { OutgoingStopMs = 160_000 });

        state.Transition!.OutgoingStopMs.Should().Be(160_000);
    }

    [Fact]
    public async Task An_offer_the_players_were_sent_is_not_replaced()
    {
        MusicPlayerState state = Play(StopMs - 5000);
        await OfferAsync(Join());
        await _playback.SendDueTransitionAsync(_user, state);

        PluginMusicTransitionAnswer answer = await OfferAsync(
            Join() with
            {
                OutgoingStopMs = 175_000,
            }
        );

        answer.Should().Be(PluginMusicTransitionAnswer.TooLate);
        state.Transition!.OutgoingStopMs.Should().Be(StopMs);
    }

    [Fact]
    public async Task A_plugin_takes_back_its_own_offer_and_not_another_plugins()
    {
        MusicPlayerState state = Play();
        await OfferAsync(Join());

        await _host.WithdrawTransitionAsync(Ulid.NewUlid(), _user.Id);
        state.Transition.Should().NotBeNull();

        await _host.WithdrawTransitionAsync(PluginId, _user.Id);
        state.Transition.Should().BeNull();
    }

    // ── When the players are sent it ─────────────────────────────────────────

    [Theory]
    [InlineData(StopMs - 10_100, false)]
    [InlineData(StopMs - 10_000, true)]
    [InlineData(StopMs - 3000, true)]
    public async Task The_players_are_sent_it_ten_seconds_before_the_stop(int positionMs, bool sent)
    {
        MusicPlayerState state = Play(positionMs);
        await OfferAsync(Join());

        await _playback.SendDueTransitionAsync(_user, state);

        _sent.Select(message => message.Name).Should().Equal(sent ? ["PrepareTransition"] : []);
        state.Transition!.Sent.Should().Be(sent);
    }

    [Fact]
    public async Task It_is_sent_once()
    {
        MusicPlayerState state = Play(StopMs - 9000);
        await OfferAsync(Join());

        await _playback.SendDueTransitionAsync(_user, state);
        state.Time += 100;
        await _playback.SendDueTransitionAsync(_user, state);

        _sent.Should().ContainSingle();
    }

    [Fact]
    public async Task The_message_carries_the_next_track_and_the_join_as_it_was_offered()
    {
        MusicPlayerState state = Play(StopMs - 9000);
        await OfferAsync(Join() with { Data = """{"bars":8,"at":"2026-01-02T03:04:05Z"}""" });

        await _playback.SendDueTransitionAsync(_user, state);

        PrepareTransitionMessage message = (PrepareTransitionMessage)_sent.Single().Payload!;
        message.NextItem.Should().BeSameAs(_next);
        message.Transition.Mime.Should().Be("audio/mp4");
        message.Transition.DurationMs.Should().Be(20_000);
        message.Transition.OutgoingStopMs.Should().Be(StopMs);
        message.Transition.IncomingResumeMs.Should().Be(ResumeMs);
        message.Timestamp.Should().BeGreaterThan(0);

        // Passed on unread: the players get the object character for character.
        string json = JsonConvert.SerializeObject(message);
        json.Should().Contain("\"data\":{\"bars\":8,\"at\":\"2026-01-02T03:04:05Z\"}");

        JObject wire = JObject.Parse(json);
        wire["transition"]!["url"]!.Value<string>().Should().StartWith("/api/v1/plugins/");
        wire.Properties()
            .Select(property => property.Name)
            .Should()
            .Equal("next_item", "transition", "timestamp");
        ((JObject)wire["transition"]!)
            .Properties()
            .Select(property => property.Name)
            .Should()
            .Equal("url", "mime", "duration_ms", "outgoing_stop_ms", "incoming_resume_ms", "data");
    }

    [Fact]
    public async Task Without_data_the_message_says_so()
    {
        MusicPlayerState state = Play(StopMs - 9000);
        await OfferAsync(Join());

        await _playback.SendDueTransitionAsync(_user, state);

        Assert.Null(((PrepareTransitionMessage)_sent.Single().Payload!).Transition.Data);
    }

    /// <summary>The link plays for the listener it was made for and for nobody else.</summary>
    [Fact]
    public async Task The_link_is_made_for_this_listener_and_plays_for_nobody_else()
    {
        MusicPlayerState state = Play(StopMs - 9000);
        await OfferAsync(Join());

        await _playback.SendDueTransitionAsync(_user, state);

        string url = ((PrepareTransitionMessage)_sent.Single().Payload!).Transition.Url;
        url.Should().StartWith($"/api/v1/plugins/{PluginId}/media/");
        string ticket = url[(url.LastIndexOf('/') + 1)..];

        _minter.Refuse(ticket, _user.Id).Should().BeNull();
        _minter.Refuse(ticket, Guid.NewGuid()).Should().NotBeNull();
        _minter.Read(ticket)!.FilePath.Should().Be("joins/a-b.m4a");
        _minter.Read(ticket)!.PluginId.Should().Be(PluginId);
    }

    /// <summary>The media route would refuse this listener, so no link is sent that cannot play.</summary>
    [Fact]
    public async Task A_listener_who_cannot_see_the_plugin_is_sent_nothing()
    {
        _access.Setup(access => access.Resolve(PluginId, _user.Id)).Returns(PluginAccess.None);
        MusicPlayerState state = Play(StopMs - 9000);
        await OfferAsync(Join());

        await _playback.SendDueTransitionAsync(_user, state);

        _sent.Should().BeEmpty();
        state.Transition.Should().BeNull();
    }

    [Fact]
    public async Task Without_an_offer_nothing_is_sent()
    {
        MusicPlayerState state = Play(StopMs - 9000);

        await _playback.SendDueTransitionAsync(_user, state);

        _sent.Should().BeEmpty();
    }

    /// <summary>The file can stand in for a stretch inside one track; the track it leads into is then the playing one.</summary>
    [Fact]
    public async Task An_offer_that_leads_back_into_the_playing_track_names_that_track_as_next()
    {
        MusicPlayerState state = Play(60_000 - 9000);
        PluginMusicTransitionAnswer answer = await OfferAsync(
            new(_playing.Id, _playing.Id, "joins/a-b.m4a", 60_000, 75_000, 20_000)
        );

        await _playback.SendDueTransitionAsync(_user, state);

        answer.Should().Be(PluginMusicTransitionAnswer.Accepted);
        ((PrepareTransitionMessage)_sent.Single().Payload!).NextItem.Should().BeSameAs(_playing);
    }

    // ── PrepareCrossfade is what it was ──────────────────────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_crossfade_signal_is_sent_when_and_as_it_always_was(bool withAnOffer)
    {
        MusicPlayerState state = Play(StopMs - 9000);
        if (withAnOffer)
        {
            await OfferAsync(Join());
            await _playback.SendDueTransitionAsync(_user, state);
        }
        _sent.Clear();

        await _playback.SendDueCrossfadeSignalAsync(_user, state, 180_000);
        _sent.Should().BeEmpty();

        state.Time = 170_000;
        await _playback.SendDueCrossfadeSignalAsync(_user, state, 180_000);
        await _playback.SendDueCrossfadeSignalAsync(_user, state, 180_000);

        (string name, object? payload) = _sent.Should().ContainSingle().Subject;
        name.Should().Be("PrepareCrossfade");
        JObject wire = JObject.Parse(JsonConvert.SerializeObject(payload));
        wire.Properties()
            .Select(property => property.Name)
            .Should()
            .Equal("next_item", "crossfade_duration_ms", "timestamp");
        wire["crossfade_duration_ms"]!.Value<int>().Should().Be(3000);
        wire["next_item"]!["id"]!.Value<string>().Should().Be(_next.Id.ToString());
    }

    // ── When the offer ends ──────────────────────────────────────────────────

    [Fact]
    public async Task A_skip_drops_the_offer_and_ends_a_fade_in_progress()
    {
        MusicPlayerState state = await PlayWithAnOfferAndAFade();

        _commands.HandleCommand(_user, "next", null, state);
        await _playback.UpdatePlaybackState(_user, state);

        state.CurrentItem.Should().BeSameAs(_next);
        state.Transition.Should().BeNull();
        state.IsCrossfading.Should().BeFalse();
    }

    [Fact]
    public async Task A_seek_past_the_stop_drops_the_offer_and_ends_a_fade_in_progress()
    {
        MusicPlayerState state = await PlayWithAnOfferAndAFade();

        _commands.HandleCommand(_user, "seek", "175", state);

        state.Transition.Should().BeNull();
        state.IsCrossfading.Should().BeFalse();
    }

    /// <summary>The listener went back: the join is ahead again, so the players are sent it again in time.</summary>
    [Fact]
    public async Task A_seek_back_to_well_before_the_stop_keeps_the_offer_and_sends_it_again()
    {
        MusicPlayerState state = Play(StopMs - 5000);
        await OfferAsync(Join());
        await _playback.SendDueTransitionAsync(_user, state);

        _commands.HandleCommand(_user, "seek", "30", state);
        await _playback.SendDueTransitionAsync(_user, state);
        _sent.Should().ContainSingle();

        state.Time = StopMs - 8000;
        await _playback.SendDueTransitionAsync(_user, state);

        _sent
            .Select(message => message.Name)
            .Should()
            .Equal("PrepareTransition", "PrepareTransition");
    }

    [Fact]
    public async Task A_change_of_the_next_track_drops_the_offer()
    {
        MusicPlayerState state = Play();
        await OfferAsync(Join());

        (await _host.MoveInQueueAsync(_user.Id, _later.Id, 0))
            .Should()
            .Be(PluginMusicQueueAnswer.Accepted);

        state.Transition.Should().BeNull();
    }

    /// <summary>The join is between the playing track and the next one. What lies behind them is no part of it.</summary>
    [Fact]
    public async Task A_change_further_down_the_list_leaves_the_offer()
    {
        MusicPlayerState state = Play();
        PlaylistTrackDto last = Track("last");
        state.Playlist = [_next, _later, last];
        await OfferAsync(Join());

        await _host.ReorderQueueAsync(_user.Id, [_next.Id, last.Id, _later.Id]);

        state.Transition.Should().NotBeNull();
    }

    /// <summary>Repeat one makes the same track the next one, though the list did not change.</summary>
    [Fact]
    public async Task A_change_of_repeat_that_changes_what_comes_next_drops_the_offer()
    {
        MusicPlayerState state = Play();
        await OfferAsync(Join());

        state.Repeat = "one";
        await _playback.UpdatePlaybackState(_user, state);

        state.Transition.Should().BeNull();
    }

    [Fact]
    public async Task Another_device_taking_over_drops_the_offer_and_ends_a_fade_in_progress()
    {
        MusicPlayerState state = await PlayWithAnOfferAndAFade();

        state.DeviceId = "tv";
        await _playback.UpdatePlaybackState(_user, state);

        state.Transition.Should().BeNull();
        state.IsCrossfading.Should().BeFalse();
    }

    [Fact]
    public async Task Playback_that_stops_drops_the_offer()
    {
        MusicPlayerState state = Play();
        await OfferAsync(Join());

        _commands.HandleCommand(_user, "stop", null, state);
        await _playback.UpdatePlaybackState(_user, state);

        state.Transition.Should().BeNull();
    }

    /// <summary>
    /// A transition no player took must not stand in the way of the next
    /// offer for the rest of the track.
    /// </summary>
    [Fact]
    public async Task An_offer_the_players_were_sent_and_did_not_take_ends_after_its_stop()
    {
        MusicPlayerState state = Play(60_000 - 9000);
        await OfferAsync(new(_playing.Id, _playing.Id, "joins/a-b.m4a", 60_000, 75_000, 20_000));
        await _playback.SendDueTransitionAsync(_user, state);

        state.Time = 60_000 + MusicJoinRules.TakenWithinMs;
        await _playback.SendDueTransitionAsync(_user, state);
        state.Transition.Should().NotBeNull();

        state.Time += 100;
        await _playback.SendDueTransitionAsync(_user, state);
        state.Transition.Should().BeNull();

        (await OfferAsync(Join())).Should().Be(PluginMusicTransitionAnswer.Accepted);
    }

    [Fact]
    public async Task An_offer_a_player_took_stands_while_its_fade_lasts()
    {
        MusicPlayerState state = Play(60_000 - 9000);
        await OfferAsync(new(_playing.Id, _playing.Id, "joins/a-b.m4a", 60_000, 75_000, 20_000));
        await _playback.SendDueTransitionAsync(_user, state);
        _playback.StartCrossfade(_user.Id, Device, 20_000);

        state.Time = 78_000;
        await _playback.SendDueTransitionAsync(_user, state);

        state.Transition.Should().NotBeNull();
        state.IsCrossfading.Should().BeTrue();
    }

    // ── The whole join ───────────────────────────────────────────────────────

    /// <summary>
    /// A plugin offers, the players are sent it, the one that sounds starts
    /// the file and says where the next track is when the file ends.
    /// </summary>
    [Fact]
    public async Task A_supplied_join_ends_with_the_next_track_playing_where_the_file_left_it()
    {
        MusicPlayerState state = Play(StopMs - 9000);
        await OfferAsync(Join());
        await _playback.SendDueTransitionAsync(_user, state);
        _playback.StartCrossfade(_user.Id, Device, 20_000);

        // Under the lock the player's tick takes, so the tick that a completed
        // join starts cannot move the position before it is read.
        await _playback.WithStateLockAsync(
            _user.Id,
            async () =>
            {
                MusicCrossfadeCompletion completion = await _playback.CompleteCrossfade(
                    _user,
                    Device,
                    _next.Id,
                    ResumeMs
                );

                completion.Should().Be(MusicCrossfadeCompletion.Completed);
                state.CurrentItem.Should().BeSameAs(_next);
                state.Time.Should().Be(ResumeMs);
                state.Transition.Should().BeNull();
                state.IsCrossfading.Should().BeFalse();
                state.CrossfadeSignalSent.Should().BeFalse();
            }
        );

        _sent
            .Select(message => message.Name)
            .Should()
            .Equal("PrepareTransition", "MusicPlayerState");
    }

    /// <summary>The same for a file that stands in for a stretch inside the playing track.</summary>
    [Fact]
    public async Task A_join_back_into_the_playing_track_ends_with_that_track_at_the_resume_position()
    {
        MusicPlayerState state = Play(60_000 - 9000);
        await OfferAsync(new(_playing.Id, _playing.Id, "joins/a-b.m4a", 60_000, 75_000, 20_000));
        await _playback.SendDueTransitionAsync(_user, state);
        _playback.StartCrossfade(_user.Id, Device, 20_000);
        state.Time = 80_000;

        await _playback.WithStateLockAsync(
            _user.Id,
            async () =>
            {
                MusicCrossfadeCompletion completion = await _playback.CompleteCrossfade(
                    _user,
                    Device,
                    _playing.Id,
                    75_000
                );

                completion.Should().Be(MusicCrossfadeCompletion.Completed);
                state.CurrentItem.Should().BeSameAs(_playing);
                state.Time.Should().Be(75_000);
                state.Playlist.Should().Equal(_next, _later);
                state.Transition.Should().BeNull();
            }
        );

        (await OfferAsync(Join())).Should().Be(PluginMusicTransitionAnswer.Accepted);
    }

    /// <summary>
    /// A hold started for one track must not keep the server from advancing
    /// out of the next one: with a long join its timeout is minutes away.
    /// </summary>
    [Fact]
    public async Task A_fade_that_was_started_for_another_track_holds_nothing_back()
    {
        MusicPlayerState state = Play();
        _playback.StartCrossfade(_user.Id, Device, 120_000);

        state.CurrentItem = _next;
        state.Playlist = [_later];
        await _playback.SendDueTransitionAsync(_user, state);

        state.IsCrossfading.Should().BeFalse();
    }

    private Task<PluginMusicTransitionAnswer> OfferAsync(PluginMusicTransition transition) =>
        _host.OfferTransitionAsync(PluginId, _user.Id, transition);

    private PluginMusicTransition Join() =>
        new(_playing.Id, _next.Id, "joins/a-b.m4a", StopMs, ResumeMs, 20_000);

    private async Task<MusicPlayerState> PlayWithAnOfferAndAFade()
    {
        MusicPlayerState state = Play(StopMs - 5000);
        await OfferAsync(Join());
        await _playback.SendDueTransitionAsync(_user, state);
        _playback.StartCrossfade(_user.Id, Device, 20_000);
        state.Transition.Should().NotBeNull();
        state.IsCrossfading.Should().BeTrue();

        return state;
    }

    private MusicPlayerState Play(int positionMs = 30_000)
    {
        MusicPlayerState state = new()
        {
            CurrentItem = _playing,
            Playlist = [_next, _later],
            PlayState = true,
            DeviceId = Device,
            Time = positionMs,
            Actions = new() { Disallows = new() },
        };
        _states.UpdateState(_user.Id, state);

        return state;
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
