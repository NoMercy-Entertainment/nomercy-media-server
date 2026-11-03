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

namespace NoMercy.NmSystem.SystemCalls;

/// <summary>
/// Production <see cref="ICommandRunner"/>: runs the tool through
/// <see cref="Shell.ExecSync(string, IReadOnlyList{string}, Shell.ExecOptions?)"/>.
/// </summary>
public sealed class ShellCommandRunner : ICommandRunner
{
    public Shell.ExecResult Run(string executable, IReadOnlyList<string> arguments) =>
        Shell.ExecSync(executable, arguments);
}
