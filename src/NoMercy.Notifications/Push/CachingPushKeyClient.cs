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

namespace NoMercy.Notifications.Push;

/// <summary>
/// Sends one notification per call site, and the SaaS's key set for this
/// server's members changes rarely, so a bare <see cref="IPushKeyClient"/>
/// would hit api.nomercy.tv on every single push. Wraps an inner client with a
/// lazily-refreshed TTL cache (no background timer — a stale cache is only
/// ever noticed, and refreshed, on the next call) and turns any failure to
/// reach the SaaS into an empty result: a self-hosted server that cannot
/// currently reach nomercy.tv must keep encoding, streaming, and everything
/// else — it just skips sending that one push. A caller-driven cancellation
/// is not "the SaaS is unreachable" and is left to propagate.
///
/// <see cref="PushDispatcher"/> can be invoked concurrently by unrelated
/// event producers (two notifications finishing at once), so a miss under
/// the TTL is not a single-caller scenario: the refresh is guarded by a lock
/// rather than trusting the last caller wins, otherwise two concurrent
/// misses would each fetch the same key set from the SaaS.
///
/// A failed fetch gets its own short TTL. Without one, an unreachable SaaS
/// means every event that follows pays the full HTTP timeout again, which is
/// exactly the storm the cache exists to prevent. The cache also keys on the
/// access token, so a re-auth serves the new principal's keys immediately
/// rather than the previous token's for the rest of the TTL.
/// </summary>
public class CachingPushKeyClient(
    IPushKeyClient inner,
    TimeSpan? ttl = null,
    TimeProvider? timeProvider = null,
    TimeSpan? failureTtl = null
) : IPushKeyClient
{
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan DefaultFailureTtl = TimeSpan.FromMinutes(1);

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly TimeSpan _ttl = ttl ?? DefaultTtl;
    private readonly TimeSpan _failureTtl = failureTtl ?? DefaultFailureTtl;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    // Token, keys and expiry live in one immutable snapshot that is replaced in a
    // single assignment once a fetch outcome is known. A cancelled fetch never
    // reaches that assignment, so the previous token's keys can never be paired
    // with the new token, and the lock-free fast path never sees a half update.
    private CacheEntry? _entry;

    public async Task<PushSubscriptionKey[]> GetKeysAsync(
        string accessToken,
        CancellationToken cancellationToken = default
    )
    {
        if (_entry is { } cached && IsFresh(cached, accessToken))
            return cached.Keys;

        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            if (_entry is { } refreshed && IsFresh(refreshed, accessToken))
                return refreshed.Keys;

            PushSubscriptionKey[] keys;
            DateTimeOffset expiresAt;
            try
            {
                keys = await inner.GetKeysAsync(accessToken, cancellationToken);
                expiresAt = _timeProvider.GetUtcNow() + _ttl;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                keys = [];
                expiresAt = _timeProvider.GetUtcNow() + _failureTtl;
            }

            _entry = new(accessToken, keys, expiresAt);
            return keys;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private bool IsFresh(CacheEntry entry, string accessToken) =>
        entry.Token == accessToken && _timeProvider.GetUtcNow() < entry.ExpiresAt;

    private sealed record CacheEntry(
        string Token,
        PushSubscriptionKey[] Keys,
        DateTimeOffset ExpiresAt
    );
}
