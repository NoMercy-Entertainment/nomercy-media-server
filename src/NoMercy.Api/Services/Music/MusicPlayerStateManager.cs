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
using System.Diagnostics.CodeAnalysis;

namespace NoMercy.Api.Services.Music;

public class MusicPlayerStateManager
{
    private readonly ConcurrentDictionary<Guid, MusicPlayerState> _playerStates = new();

    // Per-user last-issued MusicPlayerState.Seq. Clock-anchored (not a plain
    // counter) so the sequence survives a server restart without a client ever
    // seeing it go backward: the next value is always at least the current
    // wall clock, and strictly greater than the previous value even when two
    // broadcasts for the same user land in the same millisecond.
    private readonly ConcurrentDictionary<Guid, long> _lastSeq = new();

    public MusicPlayerState? GetState(Guid userId)
    {
        return _playerStates.TryGetValue(userId, out MusicPlayerState? state) ? state : null;
    }

    public void UpdateState(Guid userId, MusicPlayerState state)
    {
        _playerStates.AddOrUpdate(userId, state, (_, _) => state);
    }

    public bool RemoveState(Guid userId)
    {
        return _playerStates.TryRemove(userId, out _);
    }

    public bool HasState(Guid userId)
    {
        return _playerStates.ContainsKey(userId);
    }

    public void ClearAllStates()
    {
        _playerStates.Clear();
    }

    public bool TryGetValue(Guid userId, [NotNullWhen(true)] out MusicPlayerState? state)
    {
        return _playerStates.TryGetValue(userId, out state);
    }

    /// <summary>
    /// Issues the next <see cref="MusicPlayerState.Seq"/> for a user: strictly
    /// greater than every value previously issued to them, and never less than
    /// the current wall clock (ms since epoch) even for the first call after a
    /// server restart. <c>AddOrUpdate</c> makes the read-modify-write atomic
    /// across concurrent callers for the same user.
    /// </summary>
    public long NextSeq(Guid userId)
    {
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return _lastSeq.AddOrUpdate(
            userId,
            nowMs,
            (_, previousSeq) => Math.Max(previousSeq + 1, nowMs)
        );
    }
}
