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

using Microsoft.EntityFrameworkCore;
using Moq;
using Newtonsoft.Json;
using NoMercy.Api.DTOs.Media.Components;
using NoMercy.Api.Services;
using NoMercy.Data.Repositories;
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Media;
using Xunit;

namespace NoMercy.Tests.Api.Media;

[Trait("Category", "Home")]
public class HomeServiceEmptyStateTests
{
    private static async Task<string> EmptyHomeJson(bool canManageLibraries)
    {
        Mock<IHomeRepository> homeRepository = new();
        homeRepository
            .Setup(r =>
                r.GetHomeParallelDataAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>())
            )
            .ReturnsAsync(new HomeParallelData([], [], [], 0, 0, 0));

        HomeService service = new(
            homeRepository.Object,
            new Mock<ILibraryRepository>().Object,
            new Mock<IDbContextFactory<MediaContext>>().Object
        );

        ComponentResponse response = await service.GetHomeData(
            Guid.NewGuid(),
            "en",
            "US",
            canManageLibraries: canManageLibraries
        );

        return JsonConvert.SerializeObject(response);
    }

    [Fact]
    public async Task GetHomeData_EmptyForManagerOrOwner_OffersAddLibrary()
    {
        string json = await EmptyHomeJson(canManageLibraries: true);

        Assert.Contains("\"label\":\"Add library\"", json);
        Assert.Contains("\"route\":\"/dashboard/libraries\"", json);
    }

    [Fact]
    public async Task GetHomeData_EmptyForMember_TellsThemToAskTheOwner()
    {
        string json = await EmptyHomeJson(canManageLibraries: false);

        Assert.Contains("Ask the server owner to add a library.", json);
        Assert.DoesNotContain("Add library", json);
    }
}
