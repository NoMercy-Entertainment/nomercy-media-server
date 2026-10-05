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

namespace NoMercy.Encoder.LiveTranscode;

public class SessionManager(LiveSessionLimits limits) : ISessionManager
{
    private readonly ConcurrentDictionary<string, ILiveSession> _sessions = new();

    // Track which user owns each session — null key means anonymous
    private readonly ConcurrentDictionary<string, string> _sessionUserMap = new();
    private readonly ConcurrentDictionary<string, string> _childSessionUserMap = new();

    public IReadOnlyList<ILiveSession> ActiveSessions => [.. _sessions.Values];

    public int ActiveSessionCount => _sessions.Count;

    public bool CanStartSession(string? userId = null)
    {
        if (_sessions.Count >= limits.MaxConcurrentSessions)
            return false;

        if (userId is not null)
        {
            int userCount = _sessionUserMap.Values.Count(uid => uid == userId);
            if (userCount >= limits.MaxSessionsPerUser)
                return false;
        }

        return true;
    }

    public void RegisterSession(ILiveSession session, string? userId = null)
    {
        _sessions[session.SessionId] = session;

        if (userId is not null)
            _sessionUserMap[session.SessionId] = userId;
    }

    public void RegisterChildSession(string sessionId, string userId)
    {
        _childSessionUserMap[sessionId] = userId;
    }

    public void RemoveSession(string sessionId)
    {
        _sessions.TryRemove(sessionId, out _);
        _sessionUserMap.TryRemove(sessionId, out _);
        _childSessionUserMap.TryRemove(sessionId, out _);
    }

    public string? GetOwnerUserId(string sessionId)
    {
        _sessionUserMap.TryGetValue(sessionId, out string? userId);
        if (userId is null)
            _childSessionUserMap.TryGetValue(sessionId, out userId);
        return userId;
    }

    public IReadOnlyList<string> GetUserSessionIds(string? userId)
    {
        if (userId is null)
            return [];

        return _sessionUserMap.Where(kv => kv.Value == userId).Select(kv => kv.Key).ToList();
    }

    public void PruneDeadSessions(IReadOnlyCollection<string> aliveSessionIds)
    {
        HashSet<string> alive = [.. aliveSessionIds];

        // Union of both maps' keys — either could hold a ghost id the other has
        // already dropped.
        foreach (
            string sessionId in _sessions
                .Keys.Concat(_sessionUserMap.Keys)
                .Concat(_childSessionUserMap.Keys)
                .Distinct()
        )
        {
            if (!alive.Contains(sessionId))
                RemoveSession(sessionId);
        }
    }
}
