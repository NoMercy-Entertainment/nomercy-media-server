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

using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NoMercy.Api.Controllers.V1;
using NoMercy.Api.DTOs.Dashboard;
using NoMercy.Authorization;
using NoMercy.Data.Repositories;
using NoMercy.Database.Models.Libraries;
using NoMercy.PluginSdk.Abstractions;
using Xunit;
using MediaLibrariesController = NoMercy.Api.Controllers.V1.Media.LibrariesController;

namespace NoMercy.Tests.Api.Media;

/// <summary>
/// The library lists every member's app reads keep the server's folder paths
/// and encoder preset for the people who run it (#127). A member still gets
/// every library it can see, in the same shape, with an empty folder list.
/// </summary>
[Trait("Category", "Unit")]
public class LibraryListServerDetailsTests
{
    private static readonly Guid Caller = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Ulid PresetId = Ulid.Parse("01J9ZK5V8Y000000000000000E");

    private static Library LibraryWithAFolder()
    {
        Ulid libraryId = Ulid.NewUlid();
        Folder folder = new()
        {
            Id = Ulid.NewUlid(),
            Path = "/srv/media/movies",
            DriverId = Ulid.NewUlid(),
        };

        return new()
        {
            Id = libraryId,
            Title = "Movies",
            Type = "movie",
            EncodePresetId = PresetId,
            FolderLibraries = [new(folder.Id, libraryId) { Folder = folder }],
        };
    }

    private static ControllerContext ContextFor(bool moderator)
    {
        Mock<IMediaAuthorizationPolicy> policy = new();
        policy.Setup(one => one.IsAllowed(It.IsAny<ClaimsPrincipal>())).Returns(true);
        policy.Setup(one => one.IsModerator(It.IsAny<ClaimsPrincipal>())).Returns(moderator);

        ServiceCollection services = new();
        services.AddSingleton(policy.Object);

        return new()
        {
            HttpContext = new DefaultHttpContext
            {
                User = new(
                    new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, Caller.ToString())],
                        "test"
                    )
                ),
                RequestServices = services.BuildServiceProvider(),
            },
        };
    }

    private static Mock<ILibraryRepository> Repository()
    {
        Mock<ILibraryRepository> libraries = new();
        libraries
            .Setup(one => one.GetSetupLibrariesAsync(Caller, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => [LibraryWithAFolder()]);
        libraries
            .Setup(one => one.GetLibraries(Caller, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => [LibraryWithAFolder()]);
        return libraries;
    }

    private static async Task<List<LibrariesResponseItemDto>> ListAsync(
        string endpoint,
        bool moderator
    )
    {
        Mock<ILibraryRepository> libraries = Repository();

        IActionResult result = endpoint switch
        {
            "setup" => await new SetupController(
                Mock.Of<IAnimeThemeRepository>(),
                Mock.Of<IServerConfigurationRepository>(),
                Mock.Of<IMusicRepository>(),
                null!,
                libraries.Object,
                Mock.Of<IPluginManager>(),
                NullLogger<SetupController>.Instance
            )
            {
                ControllerContext = ContextFor(moderator),
            }.Libraries(),
            _ => await new MediaLibrariesController(
                libraries.Object,
                Mock.Of<ICollectionRepository>(),
                Mock.Of<ISpecialRepository>(),
                Mock.Of<IHomeRepository>(),
                Mock.Of<IUserPlaylistRepository>(),
                Mock.Of<IAnimeThemeRepository>(),
                Mock.Of<IAnimeDemographicRepository>(),
                Mock.Of<IAnimeSeasonRepository>(),
                Mock.Of<IServerConfigurationRepository>(),
                TimeProvider.System
            )
            {
                ControllerContext = ContextFor(moderator),
            }.Libraries(),
        };

        return result
            .Should()
            .BeOfType<OkObjectResult>()
            .Which.Value.Should()
            .BeOfType<LibrariesDto>()
            .Which.Data.ToList();
    }

    [Theory]
    [InlineData("setup")]
    [InlineData("media")]
    public async Task AMember_GetsItsLibraries_WithoutFoldersOrPreset(string endpoint)
    {
        List<LibrariesResponseItemDto> libraries = await ListAsync(endpoint, moderator: false);

        LibrariesResponseItemDto library = libraries.Should().ContainSingle().Subject;
        library.Title.Should().Be("Movies");
        library.FolderLibrary.Should().BeEmpty();
        library.EncodePresetId.Should().BeNull();
    }

    [Theory]
    [InlineData("setup")]
    [InlineData("media")]
    public async Task AModerator_StillGetsTheFoldersAndPreset(string endpoint)
    {
        List<LibrariesResponseItemDto> libraries = await ListAsync(endpoint, moderator: true);

        LibrariesResponseItemDto library = libraries.Should().ContainSingle().Subject;
        library
            .FolderLibrary.Should()
            .ContainSingle()
            .Which.Folder.Path.Should()
            .Be("/srv/media/movies");
        library.EncodePresetId.Should().Be(PresetId);
    }
}
