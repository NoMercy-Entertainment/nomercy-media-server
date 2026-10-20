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
using Xunit;

namespace NoMercy.Tests.Api.Media;

/// <summary>
/// Season and episode crew credits are stored with only SeasonId or EpisodeId,
/// so their Movie and Tv navigations are never loaded. The person response
/// must leave those rows out instead of throwing or listing a credit that
/// has no title and no id to link to.
/// </summary>
[Trait("Category", "Unit")]
public class PersonResponseItemDtoTests
{
    [Fact]
    public void Constructor_FromPerson_ListsOnlyCrewCreditsWithAMovieOrShow()
    {
        Person person = new()
        {
            Id = 5,
            Name = "Test Person",
            Crews =
            [
                new()
                {
                    SeasonId = 1,
                    Job = new() { Task = "Director" },
                },
                new()
                {
                    EpisodeId = 2,
                    Job = new() { Task = "Writer" },
                },
                new()
                {
                    TvId = 10,
                    Tv = new() { Id = 10, Title = "Test Show" },
                    Job = new() { Task = "Producer" },
                },
            ],
        };

        PersonResponseItemDto dto = new(person);

        dto.CombinedCredits.Crew.Should().ContainSingle().Which.Id.Should().Be(10);
        dto.KnownFor.Should().ContainSingle().Which.Job.Should().Be("Producer");
    }
}
