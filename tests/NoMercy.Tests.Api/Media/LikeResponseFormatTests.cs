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

using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NoMercy.Api.Controllers.V1.Media;
using NoMercy.Api.DTOs.Common;
using NoMercy.Authorization;
using NoMercy.Data.Repositories;
using Xunit;

namespace NoMercy.Tests.Api.Media;

[Trait("Category", "Unit")]
public class LikeResponseFormatTests
{
    [Theory]
    [InlineData(true, "liked")]
    [InlineData(false, "unliked")]
    public async Task MovieLike_FormatsItsSingleArgument(bool value, string expected)
    {
        Mock<IMovieRepository> repository = new();
        repository
            .Setup(r =>
                r.LikeMovieAsync(129, It.IsAny<Guid>(), value, It.IsAny<CancellationToken>())
            )
            .ReturnsAsync(true);
        MoviesController controller = new(
            repository.Object,
            Mock.Of<ILibraryRepository>(),
            Mock.Of<NoMercy.MediaProcessing.Jobs.IJobDispatcher>(),
            Mock.Of<NoMercy.Providers.TMDB.Client.IMovieMetadataProvider>(),
            Mock.Of<NoMercy.NmSystem.Information.IServerConfiguration>(),
            Mock.Of<NoMercy.Events.IEventBus>(),
            Mock.Of<Microsoft.Extensions.Logging.ILogger<MoviesController>>()
        )
        {
            ControllerContext = AllowedContext(),
        };

        IActionResult result = await controller.Like(129, new() { Value = value });

        AssertFormatted(result, expected);
    }

    [Theory]
    [InlineData(true, "liked")]
    [InlineData(false, "unliked")]
    public async Task TvLike_FormatsItsSingleArgument(bool value, string expected)
    {
        Mock<ITvShowRepository> repository = new();
        repository
            .Setup(r => r.LikeAsync(1399, It.IsAny<Guid>(), value, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        TvShowsController controller = new(
            repository.Object,
            Mock.Of<ILibraryRepository>(),
            Mock.Of<NoMercy.MediaProcessing.Jobs.IJobDispatcher>(),
            Mock.Of<NoMercy.Providers.TMDB.Client.ITvShowMetadataProvider>(),
            Mock.Of<NoMercy.Events.IEventBus>(),
            Mock.Of<NoMercy.MediaProcessing.Shows.IMediaTypeClassifier>(),
            Mock.Of<Microsoft.Extensions.Logging.ILogger<TvShowsController>>()
        )
        {
            ControllerContext = AllowedContext(),
        };

        IActionResult result = await controller.Like(1399, new() { Value = value });

        AssertFormatted(result, expected);
    }

    [Theory]
    [InlineData(true, "liked")]
    [InlineData(false, "unliked")]
    public async Task CollectionLike_FormatsItsSingleArgument(bool value, string expected)
    {
        Mock<ICollectionRepository> repository = new();
        repository
            .Setup(r => r.LikeAsync(313369, It.IsAny<Guid>(), value, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        CollectionsController controller = new(
            repository.Object,
            Mock.Of<ILibraryRepository>(),
            Mock.Of<NoMercy.MediaProcessing.Jobs.IJobDispatcher>(),
            Mock.Of<NoMercy.Providers.TMDB.Client.ICollectionMetadataProvider>(),
            Mock.Of<Microsoft.Extensions.Logging.ILogger<CollectionsController>>()
        )
        {
            ControllerContext = AllowedContext(),
        };

        IActionResult result = await controller.Like(313369, new() { Value = value });

        AssertFormatted(result, expected);
    }

    private static ControllerContext AllowedContext()
    {
        Mock<IMediaAuthorizationPolicy> policy = new();
        policy.Setup(p => p.IsAllowed(It.IsAny<ClaimsPrincipal>())).Returns(true);
        ServiceProvider services = new ServiceCollection()
            .AddSingleton(policy.Object)
            .BuildServiceProvider();
        return new()
        {
            HttpContext = new DefaultHttpContext
            {
                RequestServices = services,
                User = new ClaimsPrincipal(
                    new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())],
                        "test"
                    )
                ),
            },
        };
    }

    private static void AssertFormatted(IActionResult result, string expected)
    {
        OkObjectResult ok = Assert.IsType<OkObjectResult>(result);
        StatusResponseDto<string> response = Assert.IsType<StatusResponseDto<string>>(ok.Value);
        Assert.Single(response.Args!);
        Assert.Equal(
            expected,
            string.Format(CultureInfo.InvariantCulture, response.Message!, response.Args!)
        );
    }
}
