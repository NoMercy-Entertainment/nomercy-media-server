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

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Newtonsoft.Json.Linq;
using NoMercy.Api.Controllers.V1.Dashboard.Admin;
using NoMercy.Api.DTOs.Dashboard;
using NoMercy.Data.Repositories;
using NoMercy.Database.Models.Storage;
using NoMercy.NmSystem.Auth;
using NoMercy.NmSystem.Information;
using NoMercy.Storage;
using Xunit;

namespace NoMercy.Tests.Api.Dashboard;

public class DriversUpdateCredentialTests
{
    [Fact]
    public async Task Update_InvalidConfig_Returns400WithoutReplacingCredentials()
    {
        string? previousAppPath = Environment.GetEnvironmentVariable("NOMERCY_APP_PATH");
        string appPath = Path.Combine(AppContext.BaseDirectory, $"driver-test-{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable("NOMERCY_APP_PATH", appPath);

        try
        {
            Directory.CreateDirectory(AppFiles.SecretsPath);
            Ulid id = Ulid.NewUlid();
            string credentialRef = $"driver:{id}";
            CredentialManager.SetCredentials(
                credentialRef,
                "old-access",
                "old-secret",
                string.Empty
            );

            Driver driver = new()
            {
                Id = id,
                Name = "test-driver",
                Type = "s3",
                Config = "{\"bucket\":\"old-bucket\",\"region\":\"old-region\"}",
            };
            Mock<IDriverRepository> repository = new();
            repository.Setup(r => r.GetDriverByIdAsync(id)).ReturnsAsync(driver);
            DriversController controller = new(
                repository.Object,
                Mock.Of<IStorageFactory>(),
                NullLogger<DriversController>.Instance
            );

            IActionResult result = await controller.Update(
                id,
                new UpdateDriverRequestDto
                {
                    Config = JObject.Parse("{\"region\":\"new-region\"}"),
                    Credentials = new DriverCredentialsDto
                    {
                        AccessKey = "new-access",
                        SecretKey = "new-secret",
                    },
                }
            );

            result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(400);
            UserPass? stored = CredentialManager.Credential(credentialRef);
            stored.Should().NotBeNull();
            stored!.Username.Should().Be("old-access");
            stored.Password.Should().Be("old-secret");
            repository.Verify(r => r.UpdateDriverAsync(It.IsAny<Driver>()), Times.Never);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NOMERCY_APP_PATH", previousAppPath);
            Directory.Delete(appPath, recursive: true);
        }
    }
}
