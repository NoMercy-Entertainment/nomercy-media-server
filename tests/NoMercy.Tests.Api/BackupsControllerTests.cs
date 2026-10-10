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
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Moq;
using NoMercy.Api.Controllers.V1.Dashboard.Admin;
using NoMercy.Api.Services;
using Xunit;

namespace NoMercy.Tests.Api;

[Trait("Category", "Authorization")]
public sealed class BackupsControllerTests
{
    [Theory]
    [InlineData(nameof(BackupsController.Create), typeof(HttpPostAttribute))]
    [InlineData(nameof(BackupsController.List), typeof(HttpGetAttribute))]
    [InlineData(nameof(BackupsController.Restore), typeof(HttpPostAttribute))]
    public void BackupEndpoints_RequireOwnerPolicy(string action, Type verb)
    {
        AuthorizeAttribute policy = Assert.Single(
            typeof(BackupsController)
                .GetCustomAttributes(typeof(AuthorizeAttribute), true)
                .Cast<AuthorizeAttribute>()
        );
        Assert.Equal("Owner", policy.Policy);
        Assert.Single(typeof(BackupsController).GetMethod(action)!.GetCustomAttributes(verb, true));
    }

    [Fact]
    public async Task Restore_RestartsHostAfterApplyingBackup()
    {
        Mock<IBackupService> backups = new();
        backups
            .Setup(service => service.RestoreAsync("id", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        Mock<IHostApplicationLifetime> lifetime = new();
        BackupsController controller = new(backups.Object, lifetime.Object);

        IActionResult result = await controller.Restore("id", CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        lifetime.Verify(host => host.StopApplication(), Times.Once);
    }
}
