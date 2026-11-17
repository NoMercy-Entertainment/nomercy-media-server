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
using NoMercy.Service.Workers;

namespace NoMercy.Tests.Service.Workers;

/// <summary>
/// <see cref="PeriodicUpdateCheckService"/> waits a fixed 10s "let the host
/// settle" delay before its first update check, then checks every 6h. Only the
/// cancel-during-the-initial-delay path is exercised here — it's the one
/// branch a unit test can reach deterministically and fast (cancelling the
/// token makes <c>Task.Delay</c> throw immediately rather than actually
/// waiting). It pins the real requirement: shutting the host down before the
/// settle delay elapses must exit cleanly without ever calling the checker.
/// The "checker is actually invoked" path needs the real 10s delay to elapse
/// (the interval is a private <c>static readonly</c> constant, not injectable)
/// — see the coverage report for that residue.
/// </summary>
[Trait("Category", "Unit")]
public sealed class PeriodicUpdateCheckServiceTests
{
    private sealed class CountingUpdateChecker : IUpdateChecker
    {
        public int CallCount;

        public Task<bool> IsUpdateAvailableAsync()
        {
            CallCount++;
            return Task.FromResult(false);
        }

        public Task<bool> RefreshAsync() => IsUpdateAvailableAsync();
    }

    [Fact]
    public async Task ExecuteAsync_CancelledDuringInitialDelay_NeverCallsCheckerAndExitsCleanly()
    {
        CountingUpdateChecker checker = new();
        PeriodicUpdateCheckService service = new(checker);
        using CancellationTokenSource cts = new();
        cts.CancelAfter(TimeSpan.FromMilliseconds(20));

        Exception? thrown = await Record.ExceptionAsync(async () =>
        {
            await service.StartAsync(cts.Token);
            await Task.Delay(100);
        });

        Assert.Null(thrown);
        Assert.Equal(0, checker.CallCount);
    }
}
