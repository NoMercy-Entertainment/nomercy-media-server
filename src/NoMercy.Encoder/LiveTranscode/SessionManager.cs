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
    // One lock covers every cap decision: a slot check and the claim or
    // registration it leads to must be a single step, or two starts that run the
    // check together both see the same free slot.
    private readonly Lock _gate = new();

    private readonly ConcurrentDictionary<string, ILiveSession> _sessions = new();

    // Track which user owns each session — null key means anonymous
    private readonly ConcurrentDictionary<string, string> _sessionUserMap = new();
    private readonly ConcurrentDictionary<string, string> _childSessionUserMap = new();

    // Slots claimed by starts that have not registered their session yet. A start
    // spends seconds between its cap check and RegisterSession (the probe, the
    // encoder spawn); without these the cap only counts finished starts.
    private readonly List<SessionReservation> _pending = [];

    public IReadOnlyList<ILiveSession> ActiveSessions => [.. _sessions.Values];

    public int ActiveSessionCount => _sessions.Count;

    public bool CanStartSession(string? userId = null)
    {
        lock (_gate)
        {
            return HasFreeSlot(userId, pendingTotal: 0, pendingForUser: 0);
        }
    }

    public ISessionReservation? TryReserve(string? userId = null)
    {
        lock (_gate)
        {
            int pendingForUser = userId is null
                ? 0
                : _pending.Count(reservation => reservation.UserId == userId);
            if (!HasFreeSlot(userId, _pending.Count, pendingForUser))
                return null;

            SessionReservation reservation = new(this, userId);
            _pending.Add(reservation);
            return reservation;
        }
    }

    public void RegisterSession(ILiveSession session, string? userId = null)
    {
        lock (_gate)
        {
            _sessions[session.SessionId] = session;

            if (userId is not null)
                _sessionUserMap[session.SessionId] = userId;
        }
    }

    public void RegisterChildSession(string sessionId, string userId)
    {
        _childSessionUserMap[sessionId] = userId;
    }

    public void RemoveSession(string sessionId)
    {
        lock (_gate)
        {
            _sessions.TryRemove(sessionId, out _);
            _sessionUserMap.TryRemove(sessionId, out _);
            _childSessionUserMap.TryRemove(sessionId, out _);
        }
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

    // Caller holds _gate. Registered sessions plus the pending claims the caller
    // chooses to count: TryReserve counts them, CanStartSession does not.
    private bool HasFreeSlot(string? userId, int pendingTotal, int pendingForUser)
    {
        if (_sessions.Count + pendingTotal >= limits.MaxConcurrentSessions)
            return false;

        if (userId is null)
            return true;

        int userCount = _sessionUserMap.Values.Count(uid => uid == userId) + pendingForUser;
        return userCount < limits.MaxSessionsPerUser;
    }

    private void CompleteReservation(SessionReservation reservation, ILiveSession session)
    {
        lock (_gate)
        {
            _pending.Remove(reservation);
            RegisterSession(session, reservation.UserId);
        }
    }

    private void ReleaseReservation(SessionReservation reservation)
    {
        lock (_gate)
        {
            _pending.Remove(reservation);
        }
    }

    private sealed class SessionReservation(SessionManager owner, string? userId)
        : ISessionReservation
    {
        private bool _settled;

        public string? UserId { get; } = userId;

        public void Complete(ILiveSession session)
        {
            if (_settled)
                throw new InvalidOperationException(
                    "The live-session slot was already completed or released"
                );

            _settled = true;
            owner.CompleteReservation(this, session);
        }

        public void Dispose()
        {
            if (_settled)
                return;

            _settled = true;
            owner.ReleaseReservation(this);
        }
    }
}
