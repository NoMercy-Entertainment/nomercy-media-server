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
using NoMercy.Api.DTOs.Media;
using NoMercy.Api.Services.Video;
using NoMercy.Database.Models.Media;
using NoMercy.Database.Models.Users;
using NoMercy.NmSystem.Domain;
using Xunit;

namespace NoMercy.Tests.Api.Services.Video;

[Trait("Category", "Unit")]
public class VideoPlayerStateFactoryTests
{
    private static readonly Metadata TwoTracks = new()
    {
        Audio = [new() { Language = "jpn" }, new() { Language = "eng" }],
        Video = [new() { Width = 1920 }],
    };

    private static readonly Device Device = new() { DeviceId = "tv-1", VolumePercent = 30 };

    private static (
        VideoPlaylistResponseDto Item,
        List<VideoPlaylistResponseDto> Playlist
    ) Playlist()
    {
        VideoPlaylistResponseDto first = new() { Duration = "0" };
        VideoPlaylistResponseDto second = new() { Duration = "0" };
        return (first, [first, second]);
    }

    [Fact]
    public void UnknownUser_PlaysWithoutATrackChoice()
    {
        (VideoPlaylistResponseDto item, List<VideoPlaylistResponseDto> playlist) = Playlist();

        VideoPlayerState state = VideoPlayerStateFactory.Create(
            null,
            TwoTracks,
            Device,
            item,
            playlist,
            MediaTypes.MovieMediaType,
            129
        );

        state.CurrentAudio.Should().BeNull();
        state.Audio.Should().HaveCount(2);
        state.VolumePercentage.Should().Be(30);
        state.Actions.Disallows.Previous.Should().BeTrue();
        state.Actions.Disallows.Next.Should().BeFalse();
    }

    [Fact]
    public void UserWithoutAMatchingPreference_GetsTheFirstTracks()
    {
        (VideoPlaylistResponseDto item, List<VideoPlaylistResponseDto> playlist) = Playlist();

        VideoPlayerState state = VideoPlayerStateFactory.Create(
            new User { Id = Guid.NewGuid() },
            TwoTracks,
            Device,
            item,
            playlist,
            MediaTypes.MovieMediaType,
            129
        );

        state.CurrentAudio!.Language.Should().Be("jpn");
        state.CurrentQuality!.Width.Should().Be(1920);
    }

    [Fact]
    public void UserWithAPreferenceForThisMovie_GetsThatPreference()
    {
        (VideoPlaylistResponseDto item, List<VideoPlaylistResponseDto> playlist) = Playlist();
        User user = new()
        {
            Id = Guid.NewGuid(),
            PlaybackPreferences =
            [
                new()
                {
                    MovieId = 129,
                    Audio = new() { Language = "eng" },
                },
            ],
        };

        VideoPlayerState state = VideoPlayerStateFactory.Create(
            user,
            TwoTracks,
            Device,
            item,
            playlist,
            MediaTypes.MovieMediaType,
            129
        );

        state.CurrentAudio!.Language.Should().Be("eng");
    }
}
