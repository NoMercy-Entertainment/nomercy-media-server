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

namespace NoMercy.Encoder.Infrastructure;

/// <summary>What became of one child process.</summary>
/// <param name="ExitCode">The process's own exit code; 0 when a kill signal ended it on purpose.</param>
/// <param name="StdOut">
/// Everything the process printed to stdout, for a run without a stdout
/// callback. A run with one has already handed every line to that callback,
/// so this is then only the tail of the stream, capped like
/// <paramref name="StdErr" />.
/// </param>
/// <param name="StdErr">The tail of stderr, capped so a long run cannot hold megabytes of log lines.</param>
/// <param name="Duration">Wall-clock time from start to exit.</param>
/// <param name="ProcessId">The operating system's id of the process, 0 when it is not known.</param>
public record ProcessResult(
    int ExitCode,
    string StdOut,
    string StdErr,
    TimeSpan Duration,
    int ProcessId = 0
)
{
    public bool IsSuccess => ExitCode == 0;
}
