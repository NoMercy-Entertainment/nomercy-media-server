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
using Moq;
using NoMercy.Api.Controllers.V1.Plugins;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Access;
using NoMercy.PluginSdk.Search;
using Xunit;

namespace NoMercy.Tests.Api.Plugins;

/// <summary>
/// The caller a plugin's search sees is made by the access resolver, never by
/// a role claim in the token.
/// </summary>
[Trait("Category", "Unit")]
public class PluginSearchCallerTests
{
    private static readonly Guid Viewer = Guid.Parse("44444444-4444-4444-8444-444444444444");

    private static async Task<PluginCaller> CallerSeenBySearch(bool resolverSaysOwner)
    {
        PluginCaller? seen = null;
        Mock<IPluginSearchService> search = new();
        search
            .Setup(one =>
                one.SearchAsync(
                    It.IsAny<string>(),
                    It.IsAny<PluginCaller>(),
                    It.IsAny<TimeSpan>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Callback<string, PluginCaller, TimeSpan, CancellationToken>(
                (_, caller, _, _) => seen = caller
            )
            .ReturnsAsync([]);

        Mock<IPluginAccessResolver> owners = new();
        owners.Setup(one => one.IsOwner(Viewer)).Returns(resolverSaysOwner);

        ClaimsPrincipal user = new(
            new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, Viewer.ToString()),
                    new Claim(ClaimTypes.Role, "owner"),
                ],
                "test"
            )
        );

        PluginSearchController controller = new(search.Object, owners.Object)
        {
            ControllerContext = new() { HttpContext = new DefaultHttpContext { User = user } },
        };

        IActionResult result = await controller.Search("anything", null, default);

        result.Should().BeOfType<OkObjectResult>();
        return seen!;
    }

    [Fact]
    public async Task Search_a_role_claim_alone_does_not_make_the_caller_the_owner()
    {
        PluginCaller caller = await CallerSeenBySearch(resolverSaysOwner: false);

        caller.Role.Should().Be(PluginRole.Member);
    }

    [Fact]
    public async Task Search_the_caller_is_the_owner_when_the_resolver_says_so()
    {
        PluginCaller caller = await CallerSeenBySearch(resolverSaysOwner: true);

        caller.Role.Should().Be(PluginRole.Owner);
    }
}
