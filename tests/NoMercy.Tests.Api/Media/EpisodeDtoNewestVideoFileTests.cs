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
using NoMercy.Database.Models.Media;
using NoMercy.Database.Models.TvShows;
using NoMercy.Database.Models.Users;
using Xunit;

namespace NoMercy.Tests.Api.Media;

public class EpisodeDtoNewestVideoFileTests
{
    private static VideoFile File(string name, Ulid id) =>
        new()
        {
            Id = id,
            Filename = name,
            Folder = "/tv",
            HostFolder = "/tv",
            Duration = "00:10:00",
        };

    [Fact]
    public void Progress_ComesFromTheNewestVideoFile_NotTheOldestRow()
    {
        VideoFile older = File("/old.m3u8", Ulid.NewUlid(DateTimeOffset.UtcNow.AddDays(-30)));
        VideoFile newer = File("/new.m3u8", Ulid.NewUlid(DateTimeOffset.UtcNow));
        newer.UserData.Add(new() { Time = 300, UpdatedAt = DateTime.UtcNow });

        Episode episode = new() { Id = 1, TvId = 1 };
        episode.VideoFiles.Add(older);
        episode.VideoFiles.Add(newer);

        new EpisodeDto(episode).Progress.Should().Be(50);
    }

    [Fact]
    public void Newest_PicksTheLargestUlid_AndHonoursThePredicate()
    {
        VideoFile older = File("/old.m3u8", Ulid.NewUlid(DateTimeOffset.UtcNow.AddDays(-1)));
        VideoFile newer = File("/new.m3u8", Ulid.NewUlid(DateTimeOffset.UtcNow));
        newer.Folder = null;

        new[] { newer, older }.Newest().Should().BeSameAs(newer);
        new[] { older, newer }.Newest(v => v.Folder != null).Should().BeSameAs(older);
    }
}
