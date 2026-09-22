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
using NoMercy.PluginSdk.Abstractions;
using Xunit;

namespace NoMercy.Tests.Plugins;

public class PluginLiveContractTests
{
    private static PluginLiveChannel Channel()
    {
        return new()
        {
            Id = "npo1",
            Number = 1,
            Name = "NPO 1",
            Group = "Nederland",
            StreamKind = PluginLiveStreamKind.Hls,
            Links =
            [
                new() { Url = new("https://one.example/npo1.m3u8") },
                new() { Url = new("https://two.example/npo1.m3u8") },
            ],
        };
    }

    [Fact]
    public void A_channel_carries_a_number_a_group_and_prioritized_links()
    {
        PluginLiveChannel channel = Channel();

        channel.Number.Should().Be(1);
        channel.Group.Should().Be("Nederland");
        channel.Links.Should().HaveCount(2);
        channel
            .Links[1]
            .Url.Should()
            .Be(
                new Uri("https://two.example/npo1.m3u8"),
                "the order is the fall-through order, so the second link must stay second"
            );
    }

    [Fact]
    public void A_channel_defaults_to_video_not_adult_and_no_catch_up()
    {
        PluginLiveChannel channel = Channel();

        channel.Radio.Should().BeFalse();
        channel.Adult.Should().BeFalse();
        channel.CatchUpAsync.Should().BeNull();
    }

    [Fact]
    public void A_radio_channel_says_so_so_the_audio_player_takes_it()
    {
        PluginLiveChannel channel = Channel() with { Radio = true };

        channel.Radio.Should().BeTrue();
    }

    [Fact]
    public void Catch_up_asks_for_a_channel_and_a_window()
    {
        PluginCatchUpRequest request = new()
        {
            ChannelId = "npo1",
            Start = DateTimeOffset.Parse("2026-09-16T18:00:00Z"),
            Stop = DateTimeOffset.Parse("2026-09-16T18:30:00Z"),
        };

        (request.Stop - request.Start).Should().Be(TimeSpan.FromMinutes(30));
        request.ChannelId.Should().Be("npo1");
    }

    [Fact]
    public void A_guide_row_carries_everything_a_client_draws()
    {
        PluginEpgProgram program = new()
        {
            ChannelId = "npo1",
            Title = "Journaal",
            Start = DateTimeOffset.Parse("2026-09-16T18:00:00Z"),
            Stop = DateTimeOffset.Parse("2026-09-16T18:30:00Z"),
            Categories = ["News"],
            AgeRating = "ALL",
            IsNew = true,
        };

        program.Categories.Should().ContainSingle().Which.Should().Be("News");
        program.AgeRating.Should().Be("ALL");
        program.IsNew.Should().BeTrue();
    }

    [Fact]
    public void A_guide_row_is_not_new_and_carries_no_rating_unless_the_guide_said_so()
    {
        PluginEpgProgram program = new()
        {
            ChannelId = "npo1",
            Title = "Journaal",
            Start = DateTimeOffset.Parse("2026-09-16T18:00:00Z"),
            Stop = DateTimeOffset.Parse("2026-09-16T18:30:00Z"),
        };

        program.IsNew.Should().BeFalse("a repeat marked new is worse than one marked nothing");
        program.AgeRating.Should().BeNull();
        program.IconUrl.Should().BeNull();
    }

    [Fact]
    public void Provider_limits_state_the_connection_ceiling_the_host_shares_viewers_under()
    {
        PluginProviderLimits limits = new() { MaxConnections = 2, ShareOneUpstream = true };

        limits.MaxConnections.Should().Be(2);
        limits.ShareOneUpstream.Should().BeTrue();
    }

    [Fact]
    public void A_recording_names_a_channel_a_library_and_a_retention()
    {
        PluginRecordingRequest request = new()
        {
            ChannelId = "npo1",
            Start = DateTimeOffset.Parse("2026-09-16T18:00:00Z"),
            Stop = DateTimeOffset.Parse("2026-09-16T18:30:00Z"),
            Library = LibraryId.Parse("01J9ZK5V8Y0000000000000000"),
            Retention = new()
            {
                KeepFor = TimeSpan.FromDays(30),
                MaxBytes = 50L * 1024 * 1024 * 1024,
            },
        };

        request.Retention!.KeepFor.Should().Be(TimeSpan.FromDays(30));
        request.Library.Should().Be(LibraryId.Parse("01J9ZK5V8Y0000000000000000"));
    }

    [Fact]
    public void A_recording_reports_which_state_it_reached()
    {
        Enum.GetNames<PluginRecordingState>()
            .Should()
            .BeEquivalentTo(["Scheduled", "Recording", "Finished", "Failed"]);
    }

    [Fact]
    public void A_recording_that_runs_out_of_disk_refuses_with_its_own_code()
    {
        PluginRefusal refusal = PluginRefusalMessages.RecordingDiskFull("Live TV 1.0.0", "npo1");

        refusal.Code.Should().Be(PluginRefusalCodes.RecordingDiskFull);
        refusal.Fix.Should().Contain("/nomercy-plugins/capabilities/media-record");
    }

    [Fact]
    public void A_link_that_fails_degrades_rather_than_blocks()
    {
        PluginRefusal refusal = PluginRefusalMessages.LiveLinkFailed("Live TV 1.0.0", "npo1", 1);

        refusal
            .Severity.Should()
            .Be(
                PluginRefusalSeverity.Degraded,
                "one dead mirror out of several is not a reason to stop the channel"
            );
        refusal.What.Should().Contain("npo1");
    }

    [Fact]
    public void A_stream_is_either_a_playlist_or_a_continuous_transport_stream()
    {
        Enum.GetNames<PluginLiveStreamKind>().Should().BeEquivalentTo(["Hls", "ContinuousTs"]);
    }
}
