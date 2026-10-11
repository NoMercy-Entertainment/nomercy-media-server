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
using Moq;
using NoMercy.Api.Controllers.V1.Dashboard.Admin;
using NoMercy.Api.DTOs.Security;
using NoMercy.Api.Security;
using Xunit;

namespace NoMercy.Tests.Api.Security;

public class SecurityControllerValidationTests
{
    [Theory]
    [InlineData("max_score")]
    [InlineData("window_minutes")]
    [InlineData("ban_minutes")]
    [InlineData("max_ban_minutes")]
    [InlineData("allowlist")]
    public async Task InvalidLaterField_DoesNotPersistEarlierEnabledChange(string invalidField)
    {
        FakeConfigurationStore store = new();
        AbuseGuardSettings settings = new(store);
        SecurityController controller = new(
            Mock.Of<IAbuseGuard>(),
            settings,
            Mock.Of<IBlocklistFeedSettings>()
        );
        AbuseGuardSettingsRequest request = new() { Enabled = false };

        switch (invalidField)
        {
            case "max_score":
                request.MaxScore = 0;
                break;
            case "window_minutes":
                request.WindowMinutes = 0;
                break;
            case "ban_minutes":
                request.BanMinutes = 0;
                break;
            case "max_ban_minutes":
                request.MaxBanMinutes = 0;
                break;
            case "allowlist":
                request.Allowlist = "not-an-address";
                break;
        }

        IActionResult response = await controller.UpdateSettings(request, CancellationToken.None);

        response.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(400);
        settings.Enabled.Should().BeTrue();
        store.HasKey(AbuseGuardSettings.EnabledKey).Should().BeFalse();
    }
}
