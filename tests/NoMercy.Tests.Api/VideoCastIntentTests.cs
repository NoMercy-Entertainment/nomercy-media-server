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

using NoMercy.Api.DTOs.Media;
using NoMercy.Api.Hubs;
using NoMercy.Api.Services.Video;
using NoMercy.Setup.Cast;
using Xunit;

namespace NoMercy.Tests.Api;

// Both receivers open /{media_type}/{media_id}/watch (web navStore.ts, KMP CastLaunchPayload.kt),
// so media_id must be the show or movie, never the episode the player holds.
[Trait("Category", "Unit")]
public class VideoCastIntentTests
{
    private static VideoPlayerState State(string list, int itemId, int? season, int? episode) =>
        new()
        {
            CurrentList = new Uri(list, UriKind.Relative),
            CurrentItem = new VideoPlaylistResponseDto
            {
                Id = itemId,
                Season = season,
                Episode = episode,
            },
            Time = 261_000,
        };

    [Fact]
    public void An_episode_hands_off_as_its_show_with_season_and_episode()
    {
        CastIntent intent = VideoHub.IntentFor(State("/tv/240411/watch", 5001692, 1, 2));

        Assert.Equal("play_video", intent.Type);
        Assert.Equal("tv", intent.MediaType);
        Assert.Equal("240411", intent.MediaId);
        Assert.Equal(1, intent.Season);
        Assert.Equal(2, intent.Episode);
        Assert.Equal(261, intent.ResumeAt);
    }

    [Fact]
    public void A_movie_hands_off_as_the_movie()
    {
        CastIntent intent = VideoHub.IntentFor(State("/movie/603/watch", 603, null, null));

        Assert.Equal("movie", intent.MediaType);
        Assert.Equal("603", intent.MediaId);
        Assert.Null(intent.Season);
    }
}
