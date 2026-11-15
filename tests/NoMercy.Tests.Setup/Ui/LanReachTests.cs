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

using NoMercy.Setup.Ui;

namespace NoMercy.Tests.Setup.Ui;

// #660: macOS Local Network privacy drops LAN traffic to a server the owner has not
// allowed, while localhost keeps working. Setup must name the step to fix it.
[Trait("Category", "Unit")]
public sealed class LanReachTests
{
    private static readonly SetupAddress Lan = SetupAddress.Resolve("192.168.0.10", 7626, false);

    private static Func<string, Task<bool>> Answers(bool localhost, bool lan) =>
        url => Task.FromResult(url.Contains("localhost") ? localhost : lan);

    [Fact]
    public async Task LocalhostAnswers_LanDoesNot_IsBlocked()
    {
        Assert.True(await LanReach.IsBlockedAsync(Lan, Answers(localhost: true, lan: false)));
    }

    [Fact]
    public async Task BothAnswer_IsNotBlocked()
    {
        Assert.False(await LanReach.IsBlockedAsync(Lan, Answers(localhost: true, lan: true)));
    }

    [Fact]
    public async Task LocalhostDoesNotAnswer_IsNotCalledBlocked()
    {
        Assert.False(await LanReach.IsBlockedAsync(Lan, Answers(localhost: false, lan: false)));
    }

    [Fact]
    public async Task NoLanAddress_IsNotBlocked()
    {
        SetupAddress noLan = SetupAddress.Resolve(null, 7626, false);
        Assert.False(await LanReach.IsBlockedAsync(noLan, Answers(localhost: true, lan: false)));
    }

    [Fact]
    public void TheStep_NamesTheMacOsSetting()
    {
        Assert.Contains("Privacy & Security > Local Network", LanReach.MacOsStep);
        Assert.Contains("NoMercy MediaServer", LanReach.MacOsStep);
    }
}
