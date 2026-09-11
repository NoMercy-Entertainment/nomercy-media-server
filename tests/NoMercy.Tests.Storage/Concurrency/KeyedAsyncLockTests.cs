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
using NoMercy.Storage.Common;

namespace NoMercy.Tests.Storage.Concurrency;

/// <summary>
/// The one keyed async lock: what every caller that serializes work per
/// identifier - a content key, a path - depends on. Each test is bounded by a
/// deadline, so a regression that would hang reports as a failed test rather
/// than as a test run that stops.
/// </summary>
[Trait("Category", "Unit")]
public class KeyedAsyncLockTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task TwoAcquisitionsOfOneKey_Serialize()
    {
        KeyedAsyncLock locks = new();
        using CancellationTokenSource deadline = new(Deadline);

        IDisposable first = await locks.AcquireAsync("a", deadline.Token);

        Task<IDisposable> second = locks.AcquireAsync("a", deadline.Token);
        await Task.Delay(50, deadline.Token);
        second.IsCompleted.Should().BeFalse("the key is already held");

        first.Dispose();

        IDisposable granted = await second;
        granted.Dispose();
    }

    [Fact]
    public async Task DifferentKeys_DoNotWaitOnEachOther()
    {
        KeyedAsyncLock locks = new();
        using CancellationTokenSource deadline = new(Deadline);

        using IDisposable first = await locks.AcquireAsync("a", deadline.Token);

        Func<Task> acquiringAnother = async () =>
        {
            using IDisposable other = await locks.AcquireAsync("b", deadline.Token);
        };

        await acquiringAnother.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Dispose_ReleasesTheKeyForTheNextCaller()
    {
        KeyedAsyncLock locks = new();
        using CancellationTokenSource deadline = new(Deadline);

        using (await locks.AcquireAsync("a", deadline.Token)) { }

        Func<Task> acquiringAgain = async () =>
        {
            using IDisposable again = await locks.AcquireAsync("a", deadline.Token);
        };

        await acquiringAgain.Should().NotThrowAsync();
    }

    /// <summary>
    /// Disposing one release twice hands the key back once. A second release
    /// would be a permit this lock never issued, and the key would then admit
    /// two callers at a time - the one thing it exists to prevent, and
    /// invisible until two writers land on the same content.
    /// </summary>
    [Fact]
    public async Task DisposingTwice_ReleasesOnce()
    {
        KeyedAsyncLock locks = new();
        using CancellationTokenSource deadline = new(Deadline);

        IDisposable first = await locks.AcquireAsync("a", deadline.Token);
        first.Dispose();
        first.Dispose();

        // The key is free again, so this one takes it - and must be its only
        // holder, however many times the one before it was disposed.
        using IDisposable second = await locks.AcquireAsync("a", deadline.Token);

        Task<IDisposable> third = locks.AcquireAsync("a", deadline.Token);
        await Task.Delay(50, deadline.Token);

        third
            .IsCompleted.Should()
            .BeFalse("the double dispose must not have left a spare permit behind");
    }

    /// <summary>
    /// A waiter that gives up must not take the key with it: the holder's
    /// release still has to hand it to whoever comes next, or one cancelled
    /// call would wedge that key for the life of the process.
    /// </summary>
    [Fact]
    public async Task ACancelledWait_ThrowsAndLeavesTheLockUsable()
    {
        KeyedAsyncLock locks = new();
        using CancellationTokenSource deadline = new(Deadline);

        IDisposable held = await locks.AcquireAsync("a", deadline.Token);

        using CancellationTokenSource giveUp = new();
        Task<IDisposable> waiting = locks.AcquireAsync("a", giveUp.Token);
        await giveUp.CancelAsync();

        Func<Task> waited = () => waiting;
        await waited.Should().ThrowAsync<OperationCanceledException>();

        held.Dispose();

        Func<Task> acquiringAfterwards = async () =>
        {
            using IDisposable after = await locks.AcquireAsync("a", deadline.Token);
        };

        await acquiringAfterwards.Should().NotThrowAsync();
    }
}
