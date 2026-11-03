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
/// Seam over <see cref="Shell.ExecSync(string, IReadOnlyList{string}, Shell.ExecOptions?)"/>
/// so code that shells out to system tools (lsblk, diskutil, eject) can be
/// unit-tested against captured output instead of the host machine.
/// </summary>
public interface ICommandRunner
{
    /// <summary>
    /// Runs <paramref name="executable"/> with <paramref name="arguments"/> as
    /// discrete argv tokens and waits for it to exit. Throws when the
    /// executable cannot be started (the tool is not installed).
    /// </summary>
    Shell.ExecResult Run(string executable, IReadOnlyList<string> arguments);
}
