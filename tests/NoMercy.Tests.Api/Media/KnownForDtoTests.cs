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
using NoMercy.Database.Models.People;
using NoMercy.Database.Models.TvShows;
using NoMercy.NmSystem.Domain;
using NoMercy.Providers.TMDB.Models.People;
using Xunit;

namespace NoMercy.Tests.Api.Media;

/// <summary>
/// Pins <see cref="KnownForDto"/>'s "has item" detection against a null
/// navigation. <c>x?.Collection.Count != 0</c> is always true when <c>x</c> is
/// null (<c>null != 0</c>), so a cast/crew credit with no Movie must not be
/// misreported as having files.
/// </summary>
[Trait("Category", "Unit")]
public class KnownForDtoTests
{
    private const int ShowId = 10;

    [Fact]
    public void Constructor_FromCastWithNullMovieAndEmptyEpisodes_ReportsNoItem()
    {
        Cast cast = new()
        {
            Role = new() { Character = "Herself" },
            Movie = null,
            Tv = new()
            {
                Id = 1,
                Title = "Test Show",
                Episodes =
                [
                    new()
                    {
                        Id = 1,
                        SeasonNumber = 1,
                        EpisodeNumber = 1,
                    },
                ],
            },
        };

        KnownForDto dto = new(cast);

        dto.HasItem.Should().BeFalse();
        dto.HaveItems.Should().Be(0);
    }

    [Fact]
    public void Constructor_FromTmdbCastCredit_ShowInLibraryWithoutFiles_ReportsNoItem()
    {
        Person person = new()
        {
            Casts =
            [
                new()
                {
                    TvId = ShowId,
                    Tv = BuildShowWithOneEpisode(hasVideoFile: false),
                    Role = new() { Character = "Herself" },
                },
            ],
        };

        KnownForDto dto = new(BuildTvCredit(), person);

        dto.HasItem.Should().BeFalse("no episode of the show has a video file");
    }

    [Fact]
    public void Constructor_FromTmdbCastCredit_ShowInLibraryWithFiles_ReportsItem()
    {
        Person person = new()
        {
            Casts =
            [
                new()
                {
                    TvId = ShowId,
                    Tv = BuildShowWithOneEpisode(hasVideoFile: true),
                    Role = new() { Character = "Herself" },
                },
            ],
        };

        KnownForDto dto = new(BuildTvCredit(), person);

        dto.HasItem.Should().BeTrue();
    }

    [Fact]
    public void Constructor_FromTmdbCrewCredit_ShowInLibraryWithoutFiles_ReportsNoItem()
    {
        Person person = new()
        {
            Crews =
            [
                new()
                {
                    TvId = ShowId,
                    Tv = BuildShowWithOneEpisode(hasVideoFile: false),
                    Job = new() { Task = "Director" },
                },
            ],
        };

        KnownForDto dto = new(BuildTvCredit(), MediaTypes.TvMediaType, person);

        dto.HasItem.Should().BeFalse("no episode of the show has a video file");
    }

    [Fact]
    public void Constructor_FromTmdbCrewCredit_ShowInLibraryWithFiles_ReportsItem()
    {
        Person person = new()
        {
            Crews =
            [
                new()
                {
                    TvId = ShowId,
                    Tv = BuildShowWithOneEpisode(hasVideoFile: true),
                    Job = new() { Task = "Director" },
                },
            ],
        };

        KnownForDto dto = new(BuildTvCredit(), MediaTypes.TvMediaType, person);

        dto.HasItem.Should().BeTrue();
    }

    [Fact]
    public void Constructor_FromCrewWithOnlySeason_DoesNotThrowAndReportsNoItem()
    {
        Crew crew = new()
        {
            SeasonId = 1,
            Job = new() { Task = "Director" },
            Person = new(),
        };

        KnownForDto dto = new(crew);

        dto.Title.Should().BeNull();
        dto.Id.Should().BeNull();
        dto.Job.Should().Be("Director");
        dto.HasItem.Should().BeFalse();
        dto.HaveItems.Should().Be(0);
        dto.NumberOfItems.Should().Be(0);
    }

    private static TmdbPersonCredit BuildTvCredit()
    {
        return new()
        {
            Id = ShowId,
            Name = "Test Show",
            MediaType = MediaTypes.TvMediaType,
        };
    }

    private static Tv BuildShowWithOneEpisode(bool hasVideoFile)
    {
        Episode episode = new()
        {
            Id = 1,
            SeasonNumber = 1,
            EpisodeNumber = 1,
        };

        if (hasVideoFile)
            episode.VideoFiles.Add(new());

        return new()
        {
            Id = ShowId,
            Title = "Test Show",
            Episodes = [episode],
        };
    }
}
