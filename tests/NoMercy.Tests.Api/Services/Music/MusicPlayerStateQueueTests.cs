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
using NoMercy.Api.DTOs.Music;
using NoMercy.Api.Services.Music;
using NoMercy.Database.Models.Music;
using Xunit;

namespace NoMercy.Tests.Api.Services.Music;

[Trait("Category", "Unit")]
public class MusicPlayerStateQueueTests
{
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

    [Fact]
    public void WrapBacklogIntoPlaylist_StartsTheFirstPlayedTrackAndQueuesTheRest()
    {
        PlaylistTrackDto first = Track("first");
        PlaylistTrackDto second = Track("second");
        MusicPlayerState state = new() { Backlog = [first, second], PlayState = false };
        state.SetPosition(90_000);

        state.WrapBacklogIntoPlaylist().Should().BeTrue();

        state.CurrentItem.Should().BeSameAs(first);
        state.Playlist.Should().Equal(second);
        state.Backlog.Should().BeEmpty();
        state.PlayState.Should().BeTrue();
        state.Time.Should().Be(0);
    }

    [Fact]
    public void WrapBacklogIntoPlaylist_NothingPlayed_Stops()
    {
        MusicPlayerState state = new() { CurrentItem = Track("current"), PlayState = true };

        state.WrapBacklogIntoPlaylist().Should().BeFalse();

        state.CurrentItem.Should().BeNull();
        state.PlayState.Should().BeFalse();
        state.Playlist.Should().BeEmpty();
    }

    [Fact]
    public void SkipTo_MovesTheCurrentAndSkippedTracksToTheBacklog()
    {
        PlaylistTrackDto current = Track("current");
        PlaylistTrackDto skipped = Track("skipped");
        PlaylistTrackDto target = Track("target");
        PlaylistTrackDto after = Track("after");
        MusicPlayerState state = new()
        {
            CurrentItem = current,
            Playlist = [skipped, target, after],
            PlayState = false,
        };

        state.SkipTo(1, target);

        state.CurrentItem.Should().BeSameAs(target);
        state.Backlog.Should().Equal(current, skipped);
        state.Playlist.Should().Equal(after);
        state.PlayState.Should().BeTrue();
        state.Time.Should().Be(0);
        state.IgnoreCurrentTimeUntil.Should().BeAfter(DateTime.UtcNow);
    }
}
