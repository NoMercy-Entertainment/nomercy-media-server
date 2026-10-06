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

// NOTE ON RESIDUAL COVERAGE: ConsoleMessages.Logo()'s letter-by-letter rendering body
// picks between ConsoleLetters.Colossal and ConsoleLetters.ColossalXmas based on
// IsXmasTime() (real DateTime.Today, no injectable clock) — only the branch matching
// today's actual calendar date is reachable in a single run. Both letter tables are
// independently and fully locked by ConsoleLettersTests regardless of which one Logo()
// happens to pick, so this is a "which table" selection gap, not an untested-data gap.
