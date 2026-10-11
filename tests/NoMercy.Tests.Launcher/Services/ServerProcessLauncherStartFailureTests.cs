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

using System.Diagnostics;
using NoMercy.Launcher.Services;
using Xunit;

namespace NoMercy.Tests.Launcher.Services;

public sealed class ServerProcessLauncherStartFailureTests
{
    [Fact]
    public async Task StartServerProcessAsync_FailedStart_ClearsStateAndAllowsRetry()
    {
        ServerProcessLauncher launcher = new();
        ProcessStartInfo startInfo = MissingExecutable();

        (await launcher.StartServerProcessAsync(startInfo)).Should().BeFalse();
        launcher.IsServerProcessRunning.Should().BeFalse();
        launcher.ServerProcess.Should().BeNull();

        (await launcher.StartServerProcessAsync(startInfo)).Should().BeFalse();
        launcher.IsServerProcessRunning.Should().BeFalse();
    }

    [Fact]
    public async Task StartAppProcessAsync_FailedStart_ClearsStateAndAllowsRetry()
    {
        ServerProcessLauncher launcher = new();
        ProcessStartInfo startInfo = MissingExecutable();

        (await launcher.StartAppProcessAsync(startInfo)).Should().BeFalse();
        launcher.IsAppProcessRunning.Should().BeFalse();

        (await launcher.StartAppProcessAsync(startInfo)).Should().BeFalse();
        launcher.IsAppProcessRunning.Should().BeFalse();
    }

    private static ProcessStartInfo MissingExecutable() =>
        new(Path.Combine(Path.GetTempPath(), $"nomercy-missing-{Guid.NewGuid():N}"))
        {
            UseShellExecute = false,
        };
}
