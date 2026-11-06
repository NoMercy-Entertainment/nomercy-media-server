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

namespace NoMercy.Api.Services;

/// <summary>
/// Runs an expensive computation at most once at a time and keeps its result
/// for a while. Callers that arrive while it runs await the same task instead
/// of starting another one; a failed run is not kept.
/// </summary>
public sealed class SingleFlightCache<T>(TimeSpan ttl, Func<DateTime>? utcNow = null)
{
    private readonly object _gate = new();
    private readonly Func<DateTime> _utcNow = utcNow ?? (() => DateTime.UtcNow);
    private Task<T>? _inFlight;
    private T? _value;
    private DateTime _storedAt;
    private bool _hasValue;

    public Task<T> GetAsync(Func<Task<T>> compute)
    {
        lock (_gate)
        {
            if (_hasValue && _utcNow() - _storedAt < ttl)
                return Task.FromResult(_value!);

            if (_inFlight is not null)
                return _inFlight;

            // A run that finished synchronously has already cleared itself;
            // keeping it would pin a stale (or failed) task forever.
            Task<T> run = RunAsync(compute);
            if (!run.IsCompleted)
                _inFlight = run;
            return run;
        }
    }

    private async Task<T> RunAsync(Func<Task<T>> compute)
    {
        try
        {
            T result = await compute();
            lock (_gate)
            {
                _value = result;
                _storedAt = _utcNow();
                _hasValue = true;
            }
            return result;
        }
        finally
        {
            lock (_gate)
                _inFlight = null;
        }
    }
}
