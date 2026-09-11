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

using System.Collections.Concurrent;

namespace NoMercy.Storage.Common;

/// <summary>
/// One async lock per key: work on different keys runs side by side, work on
/// the same key queues up.
/// <para>
/// A key's semaphore is never removed. Removing one races a concurrent
/// <see cref="ConcurrentDictionary{TKey,TValue}.GetOrAdd(TKey,Func{TKey,TValue})" />
/// into minting a second gate for the same key - which is exactly the
/// serialization this exists to guarantee - so callers keep the dictionary
/// bounded by asking only about keys they actually hold work for.
/// </para>
/// <para>
/// It lives in <c>NoMercy.Storage</c> because that is the lowest assembly
/// every caller can reach: <c>NoMercy.NmSystem</c> already references this
/// one, so a home there would be out of reach for storage itself.
/// </para>
/// </summary>
public sealed class KeyedAsyncLock
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(
        StringComparer.Ordinal
    );

    /// <summary>
    /// Waits for <paramref name="key" /> and hands back the release: dispose
    /// it - a <c>using</c> does - to let the next caller in.
    /// </summary>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="ct" /> was cancelled while waiting. The key is left
    /// exactly as it was, so the holder's release still reaches whoever is
    /// next in line.
    /// </exception>
    public async Task<IDisposable> AcquireAsync(string key, CancellationToken ct = default)
    {
        SemaphoreSlim gate = _gates.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        return new Release(gate);
    }

    /// <summary>
    /// The held key, as the only thing a caller can do with it: give it back.
    /// Disposing twice releases once - a <c>using</c> inside a method that
    /// also releases by hand is a permit count this lock never had.
    /// </summary>
    private sealed class Release(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                gate.Release();
            }
        }
    }
}
