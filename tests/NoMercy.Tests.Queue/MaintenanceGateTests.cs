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

using FluentAssertions;
using NoMercy.Queue.MediaServer.Jobs;
using Xunit;

namespace NoMercy.Tests.Queue;

[Trait("Category", "Unit")]
public class MaintenanceGateTests
{
    [Fact]
    public void TryEnter_WhileAnotherHoldsIt_ReturnsNull()
    {
        MaintenanceGate gate = new();
        using IDisposable? first = gate.TryEnter();

        IDisposable? second = gate.TryEnter();

        first.Should().NotBeNull();
        second.Should().BeNull();
    }

    [Fact]
    public void TryEnter_AfterTheHolderLeaves_Succeeds()
    {
        MaintenanceGate gate = new();
        gate.TryEnter()!.Dispose();

        using IDisposable? again = gate.TryEnter();

        again.Should().NotBeNull();
    }

    [Fact]
    public async Task EnterAsync_WhileAnotherHoldsIt_WaitsUntilItLeaves()
    {
        MaintenanceGate gate = new();
        IDisposable first = gate.TryEnter()!;

        Task<IDisposable> waiting = gate.EnterAsync(CancellationToken.None);
        await Task.Delay(100);
        waiting.IsCompleted.Should().BeFalse();

        first.Dispose();
        using IDisposable second = await waiting.WaitAsync(TimeSpan.FromSeconds(5));

        gate.TryEnter().Should().BeNull();
    }
}
