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

using NoMercy.Cli.Commands;
using Xunit;

namespace NoMercy.Tests.Cli.Commands;

/// <summary>
/// REQUIREMENT: process exit codes are a contract with whatever invokes the CLI
/// (shell scripts, service supervisors, the launcher). Success must stay 0 and
/// every failure kind must keep its assigned, non-zero value so a scripted
/// caller's exit-code branching never silently changes underneath it.
/// </summary>
[Trait("Category", "Unit")]
public sealed class ExitCodeTests
{
    [Fact]
    public void Values_MatchDocumentedContract()
    {
        ((int)ExitCode.Success).Should().Be(0);
        ((int)ExitCode.ConfigurationError).Should().Be(1);
        ((int)ExitCode.ConnectionError).Should().Be(2);
        ((int)ExitCode.ServerError).Should().Be(3);
        ((int)ExitCode.Timeout).Should().Be(4);
        ((int)ExitCode.RollbackFailed).Should().Be(5);
    }
}
