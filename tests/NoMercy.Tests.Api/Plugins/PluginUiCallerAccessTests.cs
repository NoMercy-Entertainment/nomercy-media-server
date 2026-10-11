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
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NoMercy.Api.Controllers.V1.Dashboard.Plugins;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Access;
using Xunit;

namespace NoMercy.Tests.Api.Plugins;

public class PluginUiCallerAccessTests
{
    private static readonly Ulid PluginId = Ulid.Parse("01J9ZK5V8Y000000000000000C");
    private static readonly Guid Member = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedMemberAccessReachesThePluginOnBothViewRoutes(bool submit)
    {
        Mock<IUiPlugin> plugin = new();
        PluginViewRequest? received = null;
        plugin
            .Setup(one =>
                one.GetViewAsync(It.IsAny<PluginViewRequest>(), It.IsAny<CancellationToken>())
            )
            .Callback<PluginViewRequest, CancellationToken>((request, _) => received = request)
            .ReturnsAsync(new PluginView());
        plugin.SetupGet(one => one.Routes).Returns(new PluginRouteTable());

        Mock<IPluginManager> manager = new();
        manager
            .Setup(one => one.GetPluginInfo(PluginId))
            .Returns(
                new PluginInfo
                {
                    Id = PluginId,
                    Name = "Shared UI",
                    Description = "A shared plugin",
                    Version = new Version(1, 0, 0),
                    Status = PluginStatus.Active,
                    Capabilities = new() { Hooks = ["ui"] },
                }
            );
        manager.Setup(one => one.GetPluginInstance(PluginId)).Returns(plugin.Object);

        Mock<IPluginAccessResolver> resolver = new();
        resolver.Setup(one => one.Resolve(PluginId, Member)).Returns(PluginAccess.Shared);

        PluginUiController controller = new(
            manager.Object,
            resolver.Object,
            NullLogger<PluginUiController>.Instance
        )
        {
            ControllerContext = new()
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new(
                        new ClaimsIdentity(
                            [
                                new Claim(ClaimTypes.NameIdentifier, Member.ToString()),
                                new Claim(ClaimTypes.Role, "member"),
                            ],
                            "test"
                        )
                    ),
                },
            },
        };

        IActionResult result = submit
            ? await controller.Submit(PluginId, "/", null, null, default)
            : await controller.View(PluginId, "/", null, default);

        result.Should().BeOfType<OkObjectResult>();
        received.Should().NotBeNull();
        received!.Caller.Access.Should().Be(PluginAccess.Shared);
        resolver.Verify(one => one.Resolve(PluginId, Member), Times.Once);
    }
}
