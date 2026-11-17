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

namespace NoMercy.MediaProcessing.Segments;

/// <inheritdoc />
public sealed class SegmentAnalysisLocks : ISegmentAnalysisLocks
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(
        StringComparer.Ordinal
    );

    public async Task<IAsyncDisposable> AcquireAsync(string key, CancellationToken ct)
    {
        SemaphoreSlim gate = _locks.GetOrAdd(key, _ => new(1, 1));
        await gate.WaitAsync(ct);
        return new Held(gate);
    }

    private sealed class Held(SemaphoreSlim gate) : IAsyncDisposable
    {
        private int _released;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                gate.Release();
            return ValueTask.CompletedTask;
        }
    }
}
