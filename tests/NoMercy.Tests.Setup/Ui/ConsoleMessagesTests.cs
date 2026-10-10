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

using System.Reflection;
using System.Text.RegularExpressions;
using NoMercy.NmSystem.Information;
using NoMercy.Setup.Ui;

namespace NoMercy.Tests.Setup.Ui;

/// <summary>
/// Requirement: the startup banner methods must never throw regardless of whether
/// stdout is redirected — <see cref="ConsoleMessages.ServerRunning"/> and
/// <see cref="ConsoleMessages.Logo"/> render the fancy interactive banner only on a
/// real console and no-op under redirection (piped logs, a service manager capturing
/// output); <see cref="ConsoleMessages.Welcome"/> renders in either mode.
/// </summary>
/// <remarks>
/// The test host captures the welcome output through <c>Console.Out</c>.
/// </remarks>
[Trait("Category", "Unit")]
public class ConsoleMessagesTests
{
    [Theory]
    [InlineData(2026, 12, 6, false)]
    [InlineData(2026, 12, 7, true)]
    [InlineData(2026, 12, 20, true)]
    [InlineData(2027, 1, 2, true)]
    [InlineData(2027, 1, 5, true)]
    [InlineData(2027, 1, 6, false)]
    [InlineData(2026, 6, 20, false)]
    public void IsXmasTime_UsesInclusiveHolidayWindow(int year, int month, int day, bool expected)
    {
        MethodInfo? method = typeof(ConsoleMessages).GetMethod(
            "IsXmasTime",
            BindingFlags.Static | BindingFlags.NonPublic,
            [typeof(DateTime)]
        );

        Assert.NotNull(method);
        Assert.Equal(expected, method.Invoke(null, [new DateTime(year, month, day)]));
    }

    [Fact]
    public async Task ServerRunning_DoesNotThrow()
    {
        await ConsoleMessages.ServerRunning();
    }

    [Fact]
    public async Task Welcome_DoesNotThrow()
    {
        await ConsoleMessages.Welcome();
    }

    [Fact]
    public void Logo_DoesNotThrow()
    {
        ConsoleMessages.Logo();
    }

    [Fact]
    public async Task ServerRunning_ReturnsCompletedTask()
    {
        Task task = ConsoleMessages.ServerRunning();

        await task;
        Assert.True(task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Welcome_ReturnsCompletedTask()
    {
        Task task = ConsoleMessages.Welcome();

        await task;
        Assert.True(task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Welcome_PrintsTheRunningServerVersion()
    {
        TextWriter originalOutput = Console.Out;
        using StringWriter output = new();
        try
        {
            Console.SetOut(output);
            await ConsoleMessages.Welcome();
        }
        finally
        {
            Console.SetOut(originalOutput);
        }

        // Pastel adds ANSI color codes between the label and the value on a color console.
        string plain = Regex.Replace(output.ToString(), @"\x1b\[[0-9;]*m", string.Empty);
        Assert.Contains($"Version:  {Software.GetReleaseVersion()}", plain);
    }
}
