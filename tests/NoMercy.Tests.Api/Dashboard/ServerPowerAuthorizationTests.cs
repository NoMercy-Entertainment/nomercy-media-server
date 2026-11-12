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

using Microsoft.AspNetCore.Authorization;
using NoMercy.Api.Controllers.V1.Dashboard.Admin;
using Xunit;

namespace NoMercy.Tests.Api.Dashboard;

[Trait("Category", "Authorization")]
public sealed class ServerPowerAuthorizationTests
{
    [Theory]
    [InlineData(nameof(ServerController.StopServer))]
    [InlineData(nameof(ServerController.RestartServer))]
    [InlineData(nameof(ServerController.Shutdown))]
    [InlineData("GetActivity")]
    public void Power_action_requires_owner(string methodName)
    {
        AuthorizeAttribute? authorization = typeof(ServerController)
            .GetMethod(methodName)
            ?.GetCustomAttributes(typeof(AuthorizeAttribute), true)
            .Cast<AuthorizeAttribute>()
            .SingleOrDefault();

        authorization.Should().NotBeNull();
        authorization!.Policy.Should().Be("Owner");
    }
}
