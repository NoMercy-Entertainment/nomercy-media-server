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
using System.Reflection;
using NoMercy.Launcher.Services;
using Xunit;

namespace NoMercy.Tests.Launcher.Services;

public class ServerProcessLauncherExitTests
{
    [Theory]
    [InlineData("_serverProcess", "OnServerProcessExited")]
    [InlineData("_appProcess", "OnAppProcessExited")]
    public void StaleExitAfterRestart_KeepsReplacementTracked(string fieldName, string handlerName)
    {
        ServerProcessLauncher launcher = new();
        FieldInfo field = typeof(ServerProcessLauncher).GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic
        )!;
        using Process oldProcess = new();
        using Process newProcess = Process.GetCurrentProcess();

        field.SetValue(launcher, oldProcess);
        field.SetValue(launcher, null);
        field.SetValue(launcher, newProcess);

        MethodInfo? handler = typeof(ServerProcessLauncher).GetMethod(
            handlerName,
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        Assert.NotNull(handler);
        handler.Invoke(launcher, [oldProcess]);

        Assert.Same(newProcess, field.GetValue(launcher));
        Assert.True(
            fieldName == "_serverProcess"
                ? launcher.IsServerProcessRunning
                : launcher.IsAppProcessRunning
        );
    }
}
