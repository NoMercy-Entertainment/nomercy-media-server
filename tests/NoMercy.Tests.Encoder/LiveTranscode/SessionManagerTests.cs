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

using NoMercy.Encoder.Codecs;
using NoMercy.Encoder.LiveTranscode;

namespace NoMercy.Tests.Encoder.LiveTranscode;

public class SessionManagerTests
{
    private static LiveQuality MakeQuality() =>
        new(
            Id: "720p",
            Label: "720p",
            Width: 1280,
            Height: 720,
            Codec: VideoCodecType.H264,
            BitrateKbps: 4000,
            Encoder: "libx264",
            IsHardwareAccelerated: false,
            ExpectedSpeed: 1.5,
            CanRealtime: true
        );

    private static LiveSession MakeSession(string id) => new(id, MakeQuality());

    // ──────────────────────────────────────────────────────────────────────────
    // CanStartSession
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void CanStartSession_WhenEmpty_ReturnsTrue()
    {
        SessionManager manager = new(new() { MaxConcurrentSessions = 4 });

        bool result = manager.CanStartSession();

        result.Should().BeTrue();
    }

    [Fact]
    public void CanStartSession_WhenAtMaxConcurrent_ReturnsFalse()
    {
        SessionManager manager = new(new() { MaxConcurrentSessions = 2 });
        manager.RegisterSession(MakeSession("s1"));
        manager.RegisterSession(MakeSession("s2"));

        bool result = manager.CanStartSession();

        result.Should().BeFalse();
    }

    [Fact]
    public void CanStartSession_WhenUnderMax_ReturnsTrue()
    {
        SessionManager manager = new(new() { MaxConcurrentSessions = 4 });
        manager.RegisterSession(MakeSession("s1"));
        manager.RegisterSession(MakeSession("s2"));

        bool result = manager.CanStartSession();

        result.Should().BeTrue();
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Per-user limit
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void CanStartSession_WhenUserAtMax_ReturnsFalse()
    {
        SessionManager manager = new(new() { MaxConcurrentSessions = 10, MaxSessionsPerUser = 2 });
        manager.RegisterSession(MakeSession("u1-s1"), userId: "user-1");
        manager.RegisterSession(MakeSession("u1-s2"), userId: "user-1");

        bool result = manager.CanStartSession(userId: "user-1");

        result.Should().BeFalse();
    }

    [Fact]
    public void CanStartSession_DifferentUser_NotAffectedByOtherUserLimit()
    {
        SessionManager manager = new(new() { MaxConcurrentSessions = 10, MaxSessionsPerUser = 2 });
        manager.RegisterSession(MakeSession("u1-s1"), userId: "user-1");
        manager.RegisterSession(MakeSession("u1-s2"), userId: "user-1");

        bool result = manager.CanStartSession(userId: "user-2");

        result.Should().BeTrue();
    }

    [Fact]
    public void CanStartSession_NullUser_NotBoundByPerUserLimit()
    {
        SessionManager manager = new(new() { MaxConcurrentSessions = 10, MaxSessionsPerUser = 1 });
        manager.RegisterSession(MakeSession("anon-1"));
        manager.RegisterSession(MakeSession("anon-2"));

        // Anonymous sessions are not tracked per-user
        bool result = manager.CanStartSession(userId: null);

        result.Should().BeTrue();
    }

    // ──────────────────────────────────────────────────────────────────────────
    // RegisterSession / RemoveSession / ActiveSessionCount
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ActiveSessionCount_StartsAtZero()
    {
        SessionManager manager = new(new());

        manager.ActiveSessionCount.Should().Be(0);
    }

    [Fact]
    public void RegisterSession_IncreasesCount()
    {
        SessionManager manager = new(new());

        manager.RegisterSession(MakeSession("s1"));
        manager.RegisterSession(MakeSession("s2"));

        manager.ActiveSessionCount.Should().Be(2);
    }

    [Fact]
    public void RemoveSession_DecreasesCount()
    {
        SessionManager manager = new(new());
        manager.RegisterSession(MakeSession("s1"));
        manager.RegisterSession(MakeSession("s2"));

        manager.RemoveSession("s1");

        manager.ActiveSessionCount.Should().Be(1);
    }

    [Fact]
    public void RemoveSession_FreesSlot_AllowsNewSession()
    {
        SessionManager manager = new(new() { MaxConcurrentSessions = 2 });
        manager.RegisterSession(MakeSession("s1"));
        manager.RegisterSession(MakeSession("s2"));
        manager.CanStartSession().Should().BeFalse();

        manager.RemoveSession("s1");

        manager.CanStartSession().Should().BeTrue();
    }

    [Fact]
    public void RemoveSession_UnknownId_DoesNotThrow()
    {
        SessionManager manager = new(new());

        Action act = () => manager.RemoveSession("nonexistent");

        act.Should().NotThrow();
    }

    [Fact]
    public void ChildSession_HasOwnerWithoutConsumingViewerSlot_AndPrunesWithRuntime()
    {
        SessionManager manager = new(new() { MaxConcurrentSessions = 2, MaxSessionsPerUser = 2 });
        manager.RegisterSession(MakeSession("video"), "owner");
        manager.RegisterChildSession("audio", "owner");

        manager.GetOwnerUserId("audio").Should().Be("owner");
        manager.ActiveSessionCount.Should().Be(1);
        manager.CanStartSession("owner").Should().BeTrue();
        manager.GetUserSessionIds("owner").Should().ContainSingle().Which.Should().Be("video");

        manager.PruneDeadSessions(["video"]);
        manager.GetOwnerUserId("audio").Should().BeNull();
    }

    [Fact]
    public void ActiveSessions_ReflectsRegisteredSessions()
    {
        SessionManager manager = new(new());
        LiveSession session = MakeSession("s1");
        manager.RegisterSession(session);

        manager.ActiveSessions.Should().ContainSingle(s => s.SessionId == "s1");
    }

    [Fact]
    public void RemoveSession_ShouldRemoveFromActiveSessions()
    {
        SessionManager manager = new(new());
        manager.RegisterSession(MakeSession("s1"));
        manager.RegisterSession(MakeSession("s2"));

        manager.RemoveSession("s1");

        manager.ActiveSessions.Should().ContainSingle(s => s.SessionId == "s2");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // TryReserve — the slot is claimed at the check, not when the session
    // registers seconds later, so parallel starts cannot share one free slot.
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TryReserve_TwoClaimsForOneUserAtPerUserCap_GiveExactlyOneSlot()
    {
        SessionManager manager = new(new() { MaxConcurrentSessions = 10, MaxSessionsPerUser = 1 });

        ISessionReservation? first = manager.TryReserve("user-1");
        ISessionReservation? second = manager.TryReserve("user-1");

        first.Should().NotBeNull();
        second.Should().BeNull();
    }

    [Fact]
    public void TryReserve_PendingClaimCountsAgainstTheGlobalCap()
    {
        SessionManager manager = new(new() { MaxConcurrentSessions = 1, MaxSessionsPerUser = 5 });

        manager.TryReserve("user-1").Should().NotBeNull();

        manager.TryReserve("user-2").Should().BeNull();
        manager.TryReserve(userId: null).Should().BeNull();
    }

    [Fact]
    public void TryReserve_DoesNotLetAnotherUserClaimPastTheGlobalCapOfRegisteredSessions()
    {
        SessionManager manager = new(new() { MaxConcurrentSessions = 1, MaxSessionsPerUser = 5 });
        manager.RegisterSession(MakeSession("s1"), userId: "user-1");

        manager.TryReserve("user-2").Should().BeNull();
    }

    [Fact]
    public void TryReserve_DisposingAnUncompletedClaim_FreesTheSlot()
    {
        SessionManager manager = new(new() { MaxConcurrentSessions = 10, MaxSessionsPerUser = 1 });
        ISessionReservation reservation = manager.TryReserve("user-1")!;

        reservation.Dispose();

        manager.TryReserve("user-1").Should().NotBeNull();
        manager.ActiveSessionCount.Should().Be(0);
    }

    [Fact]
    public void TryReserve_CompletedClaim_BecomesTheRegisteredSessionAndKeepsTheSlot()
    {
        SessionManager manager = new(new() { MaxConcurrentSessions = 10, MaxSessionsPerUser = 1 });

        using (ISessionReservation reservation = manager.TryReserve("user-1")!)
        {
            reservation.Complete(MakeSession("u1-s1"));
        }

        manager.ActiveSessionCount.Should().Be(1);
        manager.GetOwnerUserId("u1-s1").Should().Be("user-1");
        manager.GetUserSessionIds("user-1").Should().ContainSingle().Which.Should().Be("u1-s1");
        manager.TryReserve("user-1").Should().BeNull();

        manager.RemoveSession("u1-s1");

        manager.TryReserve("user-1").Should().NotBeNull();
    }

    [Fact]
    public void TryReserve_CompletingASettledClaim_Throws()
    {
        SessionManager manager = new(new() { MaxConcurrentSessions = 10, MaxSessionsPerUser = 1 });
        ISessionReservation reservation = manager.TryReserve("user-1")!;
        reservation.Dispose();

        Action act = () => reservation.Complete(MakeSession("u1-s1"));

        act.Should().Throw<InvalidOperationException>();
        manager.ActiveSessionCount.Should().Be(0);
    }

    [Fact]
    public void TryReserve_ParallelClaims_NeverExceedTheCap()
    {
        SessionManager manager = new(new() { MaxConcurrentSessions = 3, MaxSessionsPerUser = 3 });
        int granted = 0;

        Parallel.For(
            0,
            64,
            _ =>
            {
                if (manager.TryReserve("user-1") is not null)
                    Interlocked.Increment(ref granted);
            }
        );

        granted.Should().Be(3);
    }
}
