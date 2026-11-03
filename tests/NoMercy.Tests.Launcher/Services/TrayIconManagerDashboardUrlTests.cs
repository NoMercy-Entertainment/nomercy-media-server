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

using Newtonsoft.Json;
using NoMercy.Launcher.Models;
using NoMercy.Launcher.Services;
using Xunit;

namespace NoMercy.Tests.Launcher.Services;

/// <summary>
/// "Open Dashboard" in the tray goes to the app, never to the server's own
/// address: that origin serves the API (Swagger at /), not the client
/// (issue #447). The server names the app in /manage/status as app_url, the
/// same value the setup page hands off to when setup completes.
/// </summary>
public sealed class TrayIconManagerDashboardUrlTests
{
    [Fact]
    public void DashboardUrl_IsTheAppsDashboard_NotTheServerAddress()
    {
        string json = """
            {
                "status": "running",
                "internal_address": "https://192-168-1-10.abc123.nomercy.tv:7626",
                "app_url": "https://app.nomercy.tv/"
            }
            """;
        ServerStatusResponse status = JsonConvert.DeserializeObject<ServerStatusResponse>(json)!;

        string url = TrayIconManager.BuildDashboardUrl(status.AppUrl);

        Assert.Equal("https://app.nomercy.tv/dashboard/system", url);
        Assert.DoesNotContain(status.InternalAddress!, url);
    }

    [Fact]
    public void DevServer_NamesTheDevApp()
    {
        string url = TrayIconManager.BuildDashboardUrl("https://app-dev.nomercy.tv/");

        Assert.Equal("https://app-dev.nomercy.tv/dashboard/system", url);
    }

    [Fact]
    public void OlderServerWithoutAppUrl_FallsBackToThePublicApp()
    {
        // An older server sends no app_url: still the app, never the API address.
        Assert.Equal(
            "https://app.nomercy.tv/dashboard/system",
            TrayIconManager.BuildDashboardUrl(null)
        );
        Assert.Equal(
            "https://app.nomercy.tv/dashboard/system",
            TrayIconManager.BuildDashboardUrl("")
        );
    }
}
