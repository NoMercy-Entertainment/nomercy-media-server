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
using NoMercy.Data.Plugins;

namespace NoMercy.Tests.Repositories.Plugins;

/// <summary>One RoFormer run on the GPU at a time; the next one waits its turn.</summary>
public class RoformerGpuSlotTests
{
    [Fact]
    public async Task ASecondRun_WaitsUntilTheFirstGivesTheGpuBack()
    {
        RoformerGpuSlot slot = new();
        IDisposable first = await slot.AcquireAsync(CancellationToken.None);

        Task<IDisposable> second = slot.AcquireAsync(CancellationToken.None);
        await Task.Delay(50);
        second.IsCompleted.Should().BeFalse();

        first.Dispose();
        (await second.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
    }

    [Fact]
    public async Task GivingTheGpuBackTwice_FreesItOnce()
    {
        RoformerGpuSlot slot = new();
        IDisposable first = await slot.AcquireAsync(CancellationToken.None);
        first.Dispose();
        first.Dispose();

        IDisposable second = await slot.AcquireAsync(CancellationToken.None);
        Task<IDisposable> third = slot.AcquireAsync(CancellationToken.None);
        await Task.Delay(50);

        third.IsCompleted.Should().BeFalse("the second run still holds the GPU");
        second.Dispose();
        (await third.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
    }

    [Fact]
    public async Task AWaitingRun_CanBeCancelled()
    {
        RoformerGpuSlot slot = new();
        using IDisposable first = await slot.AcquireAsync(CancellationToken.None);
        using CancellationTokenSource cancel = new();

        Task<IDisposable> second = slot.AcquireAsync(cancel.Token);
        await cancel.CancelAsync();

        Func<Task> waiting = () => second;
        await waiting.Should().ThrowAsync<OperationCanceledException>();
    }
}
