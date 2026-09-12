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
    /// call would wedge that key for the life of the process. Its claim on the
    /// entry goes with it, though - a cancelled wait that kept one would leave
    /// the key in the table for ever.
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

        locks.TrackedKeys.Should().Be(0);
    }

    /// <summary>
    /// The table is bounded by work in flight, not by every key a caller has
    /// ever mentioned: the provider cache alone addresses one key per cached
    /// URL, and a process that never forgets one leaks a semaphore apiece.
    /// </summary>
    [Fact]
    public async Task AnIdleKey_IsRemoved()
    {
        KeyedAsyncLock locks = new();
        using CancellationTokenSource deadline = new(Deadline);

        using (await locks.AcquireAsync("a", deadline.Token))
        {
            locks.TrackedKeys.Should().Be(1);
        }

        locks.TrackedKeys.Should().Be(0);
    }

    /// <summary>
    /// The other half of the same rule, and the one that makes it safe: a key
    /// somebody is still queued on is never removed, so the holder's release
    /// hands that same gate to the waiter. Handing out a fresh one instead
    /// would let two callers into a key at once - the only thing this class
    /// exists to prevent, and invisible until two writers land on one file.
    /// </summary>
    [Fact]
    public async Task AKeyWithAWaiter_IsKept()
    {
        KeyedAsyncLock locks = new();
        using CancellationTokenSource deadline = new(Deadline);

        IDisposable first = await locks.AcquireAsync("a", deadline.Token);
        Task<IDisposable> second = locks.AcquireAsync("a", deadline.Token);

        locks.TrackedKeys.Should().Be(1);

        first.Dispose();

        IDisposable granted = await second;
        locks.TrackedKeys.Should().Be(1, "the waiter it went to still holds the key");

        Task<IDisposable> third = locks.AcquireAsync("a", deadline.Token);
        await Task.Delay(50, deadline.Token);
        third.IsCompleted.Should().BeFalse("the waiter was handed the same gate, not a second one");

        granted.Dispose();
        (await third).Dispose();
        locks.TrackedKeys.Should().Be(0);
    }

    // --- The bounded synchronous acquire ---------------------------------

    /// <summary>
    /// What the callers that cannot await need: a bounded wait rather than a
    /// plain lock, because the work behind these keys is filesystem work on
    /// paths that can be an unresponsive network mount. A wait that cannot
    /// expire would hold the key for the life of the process.
    /// </summary>
    [Fact]
    public async Task Acquire_TimesOut_WhenTheKeyIsHeld()
    {
        KeyedAsyncLock locks = new();
        using CancellationTokenSource deadline = new(Deadline);

        using IDisposable held = await locks.AcquireAsync("a", deadline.Token);

        Action acquiring = () => locks.Acquire("a", TimeSpan.FromMilliseconds(50));

        acquiring.Should().Throw<TimeoutException>().WithMessage("*a*");

        locks.TrackedKeys.Should().Be(1, "the caller that gave up took its claim with it");
    }

    /// <summary>
    /// And it is the same key as the async side: a synchronous holder makes an
    /// awaiting caller wait, and its release lets that caller in.
    /// </summary>
    [Fact]
    public async Task Acquire_SerializesWithTheAsyncWaiters_AndPrunes()
    {
        KeyedAsyncLock locks = new();
        using CancellationTokenSource deadline = new(Deadline);

        IDisposable held = locks.Acquire("a", Deadline);

        Task<IDisposable> waiting = locks.AcquireAsync("a", deadline.Token);
        await Task.Delay(50, deadline.Token);
        waiting.IsCompleted.Should().BeFalse("the key is held synchronously");

        held.Dispose();

        (await waiting).Dispose();
        locks.TrackedKeys.Should().Be(0);
    }
}
