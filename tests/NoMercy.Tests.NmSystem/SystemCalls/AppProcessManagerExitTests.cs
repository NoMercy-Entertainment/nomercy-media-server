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
using NoMercy.NmSystem.SystemCalls;
using NoMercy.Storage.Drivers.Local;

namespace NoMercy.Tests.NmSystem.SystemCalls;

public class AppProcessManagerExitTests
{
    [Fact]
    public void StaleExitAfterStopAndRestart_KeepsNewAppTracked()
    {
        AppProcessManager manager = new(new LocalStorageDriver());
        FieldInfo field = typeof(AppProcessManager).GetField(
            "_appProcess",
            BindingFlags.Instance | BindingFlags.NonPublic
        )!;
        using Process oldProcess = new();
        using Process newProcess = Process.GetCurrentProcess();

        field.SetValue(manager, oldProcess);
        field.SetValue(manager, null); // Stop has released the old process.
        field.SetValue(manager, newProcess); // Start has tracked its replacement.

        MethodInfo? handler = typeof(AppProcessManager).GetMethod(
            "OnAppProcessExited",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        Assert.NotNull(handler);
        handler.Invoke(manager, [oldProcess]);

        Assert.True(manager.IsRunning);
        Assert.Equal(newProcess.Id, manager.ProcessId);
    }
}
