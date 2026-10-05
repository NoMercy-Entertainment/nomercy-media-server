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

using NoMercy.Api.DTOs.Music;
using NoMercy.Api.Services.Music;
using NoMercy.Database.Models.Music;
using Xunit;

namespace NoMercy.Tests.Api;

/// <summary>
/// duration_ms is what a client that does not parse the item's own duration
/// reads to size its scrubber. It used to be a settable field updated by hand at
/// seven call sites, and every auto-advance in MusicPlaybackService set
/// CurrentItem without it — so after the first track ended, the broadcast
/// described the new track's position against the old track's length.
///
/// Reported from the web player, which showed a full scrubber and 00:00
/// remaining while the Android clients were correct on the same broadcast
/// because they read the item instead.
/// </summary>
public class MusicStateDurationTests
{
    private static PlaylistTrackDto MakeTrack(string duration)
    {
        Track track = new()
        {
            Id = Guid.NewGuid(),
            Name = "Test Track",
            Duration = duration,
            Filename = "test.mp3",
            Folder = "/music/",
            FolderId = Ulid.NewUlid(),
        };
        return new(track, "US");
    }

    [Fact]
    public void DurationFollowsTheCurrentItem()
    {
        MusicPlayerState state = new() { CurrentItem = MakeTrack("3:00") };

        state.Duration.Should().Be(180_000);
    }

    [Fact]
    public void AdvancingToTheNextTrackTakesItsLengthWithIt()
    {
        // The defect, stated as a test: every state.CurrentItem assignment in
        // MusicPlaybackService's advance paths sets the position and nothing
        // else, so a separately-stored duration stayed on the previous track.
        MusicPlayerState state = new() { CurrentItem = MakeTrack("3:00") };
        state.Duration.Should().Be(180_000);

        state.CurrentItem = MakeTrack("7:11");

        state.Duration.Should().Be(431_000);
    }

    [Fact]
    public void NoCurrentItemIsZeroRatherThanTheLastTracksLength()
    {
        MusicPlayerState state = new() { CurrentItem = MakeTrack("3:00") };

        state.CurrentItem = null;

        state.Duration.Should().Be(0);
    }
}
