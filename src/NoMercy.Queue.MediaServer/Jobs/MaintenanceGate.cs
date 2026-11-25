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

namespace NoMercy.Queue.MediaServer.Jobs;

/// <summary>
/// The one lock the nightly maintenance jobs and the owner's clean-up share, so
/// two passes never compact or sweep at the same time.
/// </summary>
public sealed class MaintenanceGate
{
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>Takes the lock when it is free; null when a pass is running.</summary>
    public IDisposable? TryEnter() => _lock.Wait(0) ? new Lease(_lock) : null;

    public async Task<IDisposable> EnterAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        return new Lease(_lock);
    }

    private sealed class Lease(SemaphoreSlim held) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                held.Release();
        }
    }
}
