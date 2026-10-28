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

using NoMercy.NmSystem.SystemCalls;

namespace NoMercy.Tests.NmSystem.SystemCalls;

/// <summary>
/// Shell.EscapeShellArgument is the last line of defense for the handful of
/// callers that genuinely need a shell pipeline (e.g. `df ... | awk ...`)
/// and therefore can't switch to the argv-based ExecAsync overload. These
/// tests assert the actual quoting output — a weakened assertion here would
/// mask a reopened shell-injection hole.
/// </summary>
public class ShellTests
{
    [Fact]
    public void EscapeShellArgument_PlainPath_WrapsInSingleQuotes()
    {
        string result = Shell.EscapeShellArgument("/mnt/data");

        Assert.Equal("'/mnt/data'", result);
    }

    [Fact]
    public void EscapeShellArgument_PathWithSpace_StaysAsSingleToken()
    {
        string result = Shell.EscapeShellArgument("/mnt/my data");

        Assert.Equal("'/mnt/my data'", result);
    }

    [Fact]
    public void EscapeShellArgument_PathWithEmbeddedSingleQuote_UsesCloseEscapeReopen()
    {
        string result = Shell.EscapeShellArgument("/mnt/it's");

        Assert.Equal("'/mnt/it'\\''s'", result);
    }

    [Fact]
    public void EscapeShellArgument_PathWithSemicolonInjectionAttempt_IsNeutralised()
    {
        // A naive interpolation of this value into `df -T {value} | awk ...`
        // would run `rm -rf /` as a second command. Wrapped in single quotes
        // the whole payload is one inert argv token to `df`.
        string malicious = "/mnt/data; rm -rf /";

        string result = Shell.EscapeShellArgument(malicious);

        Assert.Equal("'/mnt/data; rm -rf /'", result);
        // Exactly the opening and closing quote — no unescaped quote inside
        // the payload, so a shell parsing this can't split it into a second
        // command.
        Assert.Equal(2, result.Count(c => c == '\''));
    }

    [Fact]
    public void EscapeShellArgument_BacktickInjectionAttempt_StaysLiteralInsideQuotes()
    {
        string malicious = "/mnt/`whoami`";

        string result = Shell.EscapeShellArgument(malicious);

        Assert.Equal("'/mnt/`whoami`'", result);
    }

    [Fact]
    public async Task ExecAsync_ArgvOverload_PassesArgumentContainingMetacharactersLiterally()
    {
        // A value containing shell metacharacters must reach the child
        // process as one literal argument, never re-interpreted, because
        // no shell is involved in ArgumentList-based dispatch. ';' is a
        // POSIX command separator and is left untouched by cmd.exe, so
        // this string is safe to round-trip through either platform.
        string maliciousLookingArgument = "hello; rm -rf /";

        Shell.ExecResult result = OperatingSystem.IsWindows()
            ? await Shell.ExecAsync("cmd", ["/c", "echo", maliciousLookingArgument])
            : await Shell.ExecAsync("echo", [maliciousLookingArgument]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(maliciousLookingArgument, result.StandardOutput);
    }

    [Fact]
    public async Task ExecAsync_ReadsCapturedOutputAsUtf8_WhateverTheHostCodePage()
    {
        // The child writes the UTF-8 bytes of U+2764; a host code page must not decode them.
        Shell.ExecResult result = OperatingSystem.IsWindows()
            ? await Shell.ExecAsync(
                "powershell",
                [
                    "-NoProfile",
                    "-Command",
                    "[Console]::OutputEncoding = New-Object Text.UTF8Encoding($false); [Console]::Out.Write([char]0x2764)",
                ]
            )
            : await Shell.ExecAsync("printf", ["\\342\\235\\244"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("❤", result.StandardOutput);
    }
}
