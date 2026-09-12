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

namespace NoMercy.Storage.Common;

/// <summary>
/// One async lock per key: work on different keys runs side by side, work on
/// the same key queues up.
/// <para>
/// The table is bounded by work in flight rather than by every key a caller
/// has ever mentioned. Each acquire - waiting or holding - claims its key's
/// gate, and the release that brings a key's claims to zero removes it. The
/// counting happens under the dictionary's own lock, so a gate that is being
/// handed out always has a claim on it and can never be the one removed: that
/// is what keeps this from minting a second gate for a key somebody is still
/// queued on, which would let two callers into it at once.
/// </para>
/// <para>
/// A pruned gate's <see cref="SemaphoreSlim" /> is deliberately not disposed:
/// it holds nothing unmanaged unless its wait handle is asked for, which this
/// class never does, and disposing one would race a holder that is still on
/// its way to releasing it.
/// </para>
/// <para>
/// It lives in <c>NoMercy.Storage</c> because that is the lowest assembly
/// every caller can reach: <c>NoMercy.NmSystem</c> already references this
/// one, so a home there would be out of reach for storage itself.
/// </para>
/// </summary>
public sealed class KeyedAsyncLock
{
    private readonly Dictionary<string, Gate> _gates = new(StringComparer.Ordinal);

    /// <summary>
    /// How many keys the table holds right now. For the tests that prove the
    /// pruning; a caller has no use for it, since the answer is stale the
    /// moment it is read.
    /// </summary>
    internal int TrackedKeys
    {
        get
        {
            lock (_gates)
            {
                return _gates.Count;
            }
        }
    }

    /// <summary>
    /// Waits for <paramref name="key" /> and hands back the release: dispose
    /// it - a <c>using</c> does - to let the next caller in.
    /// </summary>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="ct" /> was cancelled while waiting. The key is left
    /// exactly as it was, so the holder's release still reaches whoever is
    /// next in line; only this caller's own claim on the entry goes with it.
    /// </exception>
    public async Task<IDisposable> AcquireAsync(string key, CancellationToken ct = default)
    {
        Gate gate = Claim(key);

        try
        {
            await gate.Semaphore.WaitAsync(ct);
        }
        catch
        {
            Return(key, gate);
            throw;
        }

        return new Holding(this, key, gate);
    }

    /// <summary>
    /// The same key for a caller that cannot await - and with a bound on the
    /// wait, because the work behind these keys is filesystem work: a create
    /// against an unresponsive network mount can block indefinitely, and a
    /// wait that cannot expire would then hold the key for the life of the
    /// process. A timeout is an ordinary failure the job queue's own
    /// retry/dead-letter path already handles.
    /// </summary>
    /// <exception cref="TimeoutException">
    /// The key was still held when <paramref name="timeout" /> ran out. The
    /// message names the key, which is what the caller was waiting on - a
    /// directory, a cache file, a content hash.
    /// </exception>
    public IDisposable Acquire(string key, TimeSpan timeout)
    {
        Gate gate = Claim(key);

        bool entered;
        try
        {
            entered = gate.Semaphore.Wait(timeout);
        }
        catch
        {
            Return(key, gate);
            throw;
        }

        if (!entered)
        {
            Return(key, gate);
            throw new TimeoutException($"Timed out after {timeout} waiting for the lock on {key}");
        }

        return new Holding(this, key, gate);
    }

    /// <summary>
    /// The key's gate, with this caller counted on it. Taken before the wait,
    /// so a waiter keeps the entry alive for as long as it is queued.
    /// </summary>
    private Gate Claim(string key)
    {
        lock (_gates)
        {
            if (!_gates.TryGetValue(key, out Gate? gate))
            {
                gate = new Gate();
                _gates[key] = gate;
            }

            gate.Claims++;
            return gate;
        }
    }

    /// <summary>
    /// Gives one claim back, and removes the key when it was the last one. The
    /// reference check is what keeps a late release from taking away a gate
    /// some later caller has already minted for the same key.
    /// </summary>
    private void Return(string key, Gate gate)
    {
        lock (_gates)
        {
            gate.Claims--;
            if (gate.Claims > 0)
            {
                return;
            }

            if (_gates.TryGetValue(key, out Gate? current) && ReferenceEquals(current, gate))
            {
                _gates.Remove(key);
            }
        }
    }

    /// <summary>One key's gate, and how many callers hold or await it.</summary>
    private sealed class Gate
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        /// <summary>Read and written only under the dictionary's own lock.</summary>
        public int Claims { get; set; }
    }

    /// <summary>
    /// The held key, as the only thing a caller can do with it: give it back.
    /// Disposing twice releases once - a <c>using</c> inside a method that
    /// also releases by hand is a permit count this lock never had, and it
    /// would hand back a claim this holder does not have either.
    /// </summary>
    private sealed class Holding(KeyedAsyncLock owner, string key, Gate gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
            {
                return;
            }

            // The permit first, the claim after: a waiter holds a claim of its
            // own, so the key cannot be pruned out from under the release that
            // is about to let that waiter in.
            gate.Semaphore.Release();
            owner.Return(key, gate);
        }
    }
}
