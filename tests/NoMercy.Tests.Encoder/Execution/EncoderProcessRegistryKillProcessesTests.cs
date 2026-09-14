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

using NoMercy.Encoder.Execution;

namespace NoMercy.Tests.Encoder.Execution;

/// <summary>
/// Covers <see cref="EncoderProcessRegistry.KillProcesses"/> — the method a
/// controller calls instead of reaching into
/// <see cref="System.Diagnostics.Process"/> itself. A pid that does not
/// correspond to a live process (as every pid here is, by construction) must
/// still clear the registry entry rather than throw.
/// </summary>
public class EncoderProcessRegistryKillProcessesTests
{
    private static EncoderProcessRegistry MakeRegistry() => new();

    [Fact]
    public void KillProcesses_NoRegisteredProcesses_IsANoOp()
    {
        EncoderProcessRegistry registry = MakeRegistry();

        registry.KillProcesses(jobId: 1);

        registry.GetProcessIds(1).Should().BeEmpty();
    }

    [Fact]
    public void KillProcesses_AStalePid_IsUnregisteredRatherThanThrowing()
    {
        EncoderProcessRegistry registry = MakeRegistry();
        // A pid vanishingly unlikely to name a live process on any test runner.
        registry.Register(jobId: 1, processId: 999_999);

        registry.KillProcesses(1);

        registry.GetProcessIds(1).Should().BeEmpty();
        registry.ActiveJobIds.Should().NotContain(1);
    }

    [Fact]
    public void KillProcesses_LeavesOtherJobsUntouched()
    {
        EncoderProcessRegistry registry = MakeRegistry();
        registry.Register(jobId: 1, processId: 999_999);
        registry.Register(jobId: 2, processId: 999_998);

        registry.KillProcesses(1);

        registry.GetProcessIds(2).Should().ContainSingle().Which.Should().Be(999_998);
    }
}
