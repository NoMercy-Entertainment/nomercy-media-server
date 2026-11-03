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

namespace NoMercy.Tests.NmSystem.SystemCalls.OpticalDrives;

/// <summary>
/// Scripted <see cref="ICommandRunner"/>: answers each command line
/// ("diskutil info disk4s0") with a canned result, records every call, and
/// can simulate a missing tool by throwing.
/// </summary>
internal sealed class FakeCommandRunner : ICommandRunner
{
    private readonly Dictionary<string, Shell.ExecResult> _responses = new(StringComparer.Ordinal);
    private Exception? _failure;

    public List<string> Calls { get; } = [];

    public FakeCommandRunner Respond(string commandLine, string standardOutput, int exitCode = 0)
    {
        _responses[commandLine] = new() { ExitCode = exitCode, StandardOutput = standardOutput };
        return this;
    }

    public FakeCommandRunner FailWith(Exception failure)
    {
        _failure = failure;
        return this;
    }

    public Shell.ExecResult Run(string executable, IReadOnlyList<string> arguments)
    {
        string commandLine = string.Join(' ', [executable, .. arguments]);
        Calls.Add(commandLine);

        if (_failure is not null)
            throw _failure;

        return _responses.TryGetValue(commandLine, out Shell.ExecResult? result)
            ? result
            : new() { ExitCode = 0, StandardOutput = string.Empty };
    }
}
