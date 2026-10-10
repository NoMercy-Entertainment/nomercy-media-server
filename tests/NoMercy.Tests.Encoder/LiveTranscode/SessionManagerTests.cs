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
}
