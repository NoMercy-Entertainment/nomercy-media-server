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
/// The tray shows the setup phase words the server sends in /manage/status
/// (setup_label) and keeps no table of its own (issue #438). An older server
/// sends no setup_label, so the tray falls back to the raw phase name.
/// </summary>
public sealed class TrayIconManagerSetupLabelTests
{
    [Fact]
    public void Failed_ShowsSetupNeedsAttention_FromTheServerLabel()
    {
        string json =
            """{ "status": "starting", "setup_phase": "Failed", "setup_label": "Setup needs attention" }""";
        ServerStatusResponse status = JsonConvert.DeserializeObject<ServerStatusResponse>(json)!;

        string label = TrayIconManager.ResolveSetupPhaseLabel(status.SetupPhase, status.SetupLabel);

        Assert.Equal("Setup needs attention", label);
    }

    [Fact]
    public void StatusWithLabel_UsesTheLabel()
    {
        string label = TrayIconManager.ResolveSetupPhaseLabel(
            "Registered",
            "Getting a secure address"
        );

        Assert.Equal("Getting a secure address", label);
    }

    [Fact]
    public void StatusWithoutLabel_FallsBackToThePhaseName()
    {
        // An older server has no setup_label: never a blank tooltip, never "Starting".
        Assert.Equal("Registered", TrayIconManager.ResolveSetupPhaseLabel("Registered", null));
        Assert.Equal("Failed", TrayIconManager.ResolveSetupPhaseLabel("Failed", ""));
    }

    [Fact]
    public void NoPhaseAtAll_IsEmpty()
    {
        Assert.Equal("", TrayIconManager.ResolveSetupPhaseLabel(null, null));
    }
}
