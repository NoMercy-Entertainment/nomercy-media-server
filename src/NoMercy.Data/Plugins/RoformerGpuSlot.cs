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

namespace NoMercy.Data.Plugins;

/// <summary>
/// The host's <see cref="IRoformerGpuSlot" />: one RoFormer run at a time,
/// the next one waiting its turn. A singleton, so every plugin and the stems
/// job share it.
/// </summary>
public sealed class RoformerGpuSlot : IRoformerGpuSlot
{
    private readonly SemaphoreSlim _slot = new(1, 1);

    public async Task<IDisposable> AcquireAsync(CancellationToken ct)
    {
        await _slot.WaitAsync(ct);
        return new Held(_slot);
    }

    /// <summary>The slot, given back once: a second dispose does nothing.</summary>
    private sealed class Held(SemaphoreSlim slot) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                slot.Release();
        }
    }
}
