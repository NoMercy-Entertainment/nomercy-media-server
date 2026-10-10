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

using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NoMercy.Encoder.Codecs;
using NoMercy.Encoder.LiveTranscode;
using NoMercy.Tests.Encoder.Storage;

namespace NoMercy.Tests.Encoder.LiveTranscode;

public class LiveStreamingServiceTests
{
    private static LiveQuality MakeQuality() =>
        new(
            Id: "720p",
            Label: "720p",
            Width: 1280,
            Height: 720,
            Codec: VideoCodecType.H264,
            BitrateKbps: 3000,
            Encoder: "libx264",
            IsHardwareAccelerated: false,
            ExpectedSpeed: 2.0,
            CanRealtime: true
        );

    private static LiveSession MakeSession(string id = "sess-001") => new(id, MakeQuality());

    private static LiveStreamingService NewService()
    {
        NoMercy.Storage.IStorage storage = TestStorageFactory.CreateLocal();
        return new(
            NullLogger<LiveStreamingService>.Instance,
            storage,
            TestStorageFactory.CreateSegmentInventory(storage)
        );
    }

    [Fact]
    public void Register_StoresRuntimeReachableViaTryGet()
    {
        LiveStreamingService svc = NewService();
        LiveSession session = MakeSession();

        svc.Register(session, TimeSpan.FromSeconds(6));

        svc.TryGetRuntime(session.SessionId, out LiveRuntimeSession runtime).Should().BeTrue();
        runtime.Session.Should().BeSameAs(session);
        runtime.TargetSegmentDuration.Should().Be(TimeSpan.FromSeconds(6));
    }

    [Fact]
    public void Register_TwiceForSameSessionId_Throws()
    {
        LiveStreamingService svc = NewService();
        LiveSession session = MakeSession();
        svc.Register(session, TimeSpan.FromSeconds(6));

        Action act = () => svc.Register(session, TimeSpan.FromSeconds(6));

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task Drainer_BuffersSegmentsIndexedByIndex()
    {
        LiveStreamingService svc = NewService();
        LiveSession session = MakeSession();
        svc.Register(session, TimeSpan.FromSeconds(6));

        Segment seg0 = new(0, TimeSpan.Zero, TimeSpan.FromSeconds(6), "/tmp/0.ts", 100);
        Segment seg1 = new(1, TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(6), "/tmp/1.ts", 100);
        session.PushSegment(seg0);
        session.PushSegment(seg1);
        session.Complete();

        await WaitForBufferAsync(svc, session.SessionId, expectedCount: 2);

        svc.TryGetRuntime(session.SessionId, out LiveRuntimeSession runtime).Should().BeTrue();
        runtime.TryGetSegment(0, out Segment found0).Should().BeTrue();
        found0.Should().Be(seg0);
        runtime.TryGetSegment(1, out Segment found1).Should().BeTrue();
        found1.Should().Be(seg1);
        runtime.TryGetSegment(99, out _).Should().BeFalse();
    }

    [Fact]
    public async Task Drainer_CompletesWhenChannelCompletes()
    {
        LiveStreamingService svc = NewService();
        LiveSession session = MakeSession();
        svc.Register(session, TimeSpan.FromSeconds(6));

        session.Complete();

        await WaitForConditionAsync(() =>
        {
            return svc.TryGetRuntime(session.SessionId, out LiveRuntimeSession r) && r.IsComplete;
        });

        svc.TryGetRuntime(session.SessionId, out LiveRuntimeSession runtime).Should().BeTrue();
        runtime.IsComplete.Should().BeTrue();
    }

    [Fact]
    public async Task SnapshotSegments_ReturnsSegmentsInIndexOrder()
    {
        LiveStreamingService svc = NewService();
        LiveSession session = MakeSession();
        svc.Register(session, TimeSpan.FromSeconds(6));

        session.PushSegment(
            new(2, TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(6), "/tmp/2.ts", 1)
        );
        session.PushSegment(new(0, TimeSpan.Zero, TimeSpan.FromSeconds(6), "/tmp/0.ts", 1));
        session.PushSegment(
            new(1, TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(6), "/tmp/1.ts", 1)
        );
        session.Complete();

        await WaitForBufferAsync(svc, session.SessionId, expectedCount: 3);

        svc.TryGetRuntime(session.SessionId, out LiveRuntimeSession runtime).Should().BeTrue();
        IReadOnlyList<Segment> snap = runtime.SnapshotSegments();
        snap.Select(s => s.Index).Should().Equal(0, 1, 2);
    }

    [Fact]
    public async Task RemoveAsync_DisposesSessionAndRemovesFromMap()
    {
        LiveStreamingService svc = NewService();
        LiveSession session = MakeSession();
        svc.Register(session, TimeSpan.FromSeconds(6));

        await svc.RemoveAsync(session.SessionId);

        svc.TryGetRuntime(session.SessionId, out _).Should().BeFalse();
        session.State.Should().Be(LiveSessionState.Ended);
    }

    [Fact]
    public async Task RemoveAsync_DeletesScratchDirectory_WhenProvided()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"live-scratch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        await File.WriteAllTextAsync(Path.Combine(tempDir, "seg_00000.ts"), "data");

        try
        {
            LiveStreamingService svc = NewService();
            LiveSession session = MakeSession();
            svc.Register(session, TimeSpan.FromSeconds(6), tempDir);

            await svc.RemoveAsync(session.SessionId);

            Directory.Exists(tempDir).Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task RemoveAsync_MissingScratchDirectory_SwallowsError()
    {
        string nonExistent = Path.Combine(Path.GetTempPath(), $"live-missing-{Guid.NewGuid():N}");

        LiveStreamingService svc = NewService();
        LiveSession session = MakeSession();
        svc.Register(session, TimeSpan.FromSeconds(6), nonExistent);

        Func<Task> act = () => svc.RemoveAsync(session.SessionId);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public void GetActiveSessions_NoSessions_ReturnsEmpty()
    {
        LiveStreamingService svc = NewService();

        IReadOnlyList<LiveSessionSnapshot> result = svc.GetActiveSessions();

        result.Should().BeEmpty();
    }

    [Fact]
    public void GetActiveSessions_WithRegisteredSession_ReturnsIt()
    {
        LiveStreamingService svc = NewService();
        LiveSession session = MakeSession("snap-001");
        svc.Register(session, TimeSpan.FromSeconds(6));

        IReadOnlyList<LiveSessionSnapshot> result = svc.GetActiveSessions();

        result.Should().HaveCount(1);
        LiveSessionSnapshot snap = result[0];
        snap.SessionId.Should().Be("snap-001");
        snap.QualityId.Should().Be("720p");
        snap.Width.Should().Be(1280);
        snap.Height.Should().Be(720);
        snap.BitrateKbps.Should().Be(3000);
        snap.IsComplete.Should().BeFalse();
    }

    [Fact]
    public async Task GetActiveSessions_AfterRemoval_ExcludesRemoved()
    {
        LiveStreamingService svc = NewService();
        LiveSession s1 = MakeSession("remove-a");
        LiveSession s2 = MakeSession("remove-b");

        svc.Register(s1, TimeSpan.FromSeconds(6));
        svc.Register(s2, TimeSpan.FromSeconds(6));

        await svc.RemoveAsync("remove-a");

        IReadOnlyList<LiveSessionSnapshot> result = svc.GetActiveSessions();

        result.Should().HaveCount(1);
        result[0].SessionId.Should().Be("remove-b");
    }

    [Fact]
    public async Task ActiveSessionIds_ReflectsRegistrationAndRemoval()
    {
        LiveStreamingService svc = NewService();
        LiveSession s1 = MakeSession("a");
        LiveSession s2 = MakeSession("b");

        svc.Register(s1, TimeSpan.FromSeconds(6));
        svc.Register(s2, TimeSpan.FromSeconds(6));
        svc.ActiveSessionIds.Should().BeEquivalentTo("a", "b");

        await svc.RemoveAsync("a");
        svc.ActiveSessionIds.Should().BeEquivalentTo("b");
    }

    [Fact]
    public void Register_AsAudioRenditionChild_FlagsRuntime()
    {
        LiveStreamingService svc = NewService();
        LiveSession session = MakeSession("child-1");

        svc.Register(session, TimeSpan.FromSeconds(6), isAudioRenditionChild: true);

        svc.TryGetRuntime("child-1", out LiveRuntimeSession runtime).Should().BeTrue();
        runtime.IsAudioRenditionChild.Should().BeTrue();
    }

    [Fact]
    public async Task RemoveAsync_CascadesToChildAudioSessions()
    {
        LiveStreamingService svc = NewService();
        LiveSession parent = MakeSession("parent");
        LiveSession childA = MakeSession("audio-eng");
        LiveSession childB = MakeSession("audio-jpn");

        svc.Register(parent, TimeSpan.FromSeconds(6));
        svc.Register(childA, TimeSpan.FromSeconds(6), isAudioRenditionChild: true);
        svc.Register(childB, TimeSpan.FromSeconds(6), isAudioRenditionChild: true);
        svc.StampChildAudioSessions("parent", ["audio-eng", "audio-jpn"]);

        await svc.RemoveAsync("parent");

        // Removing the video session disposes its per-language audio children too,
        // so a switch can never target an audio track whose video is already gone.
        svc.TryGetRuntime("parent", out _).Should().BeFalse();
        svc.TryGetRuntime("audio-eng", out _).Should().BeFalse();
        svc.TryGetRuntime("audio-jpn", out _).Should().BeFalse();
        childA.State.Should().Be(LiveSessionState.Ended);
        childB.State.Should().Be(LiveSessionState.Ended);
    }

    [Fact]
    public async Task RemoveAsync_FailingAudioChild_StillDisposesSiblingAndParentAndDeletesScratch()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"live-scratch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            LiveStreamingService svc = NewService();
            LiveSession parent = MakeSession("parent");
            Mock<ILiveSession> failingChild = MakeFailingSession("audio-failing");
            LiveSession sibling = MakeSession("audio-sibling");
            svc.Register(parent, TimeSpan.FromSeconds(6), tempDir);
            svc.Register(failingChild.Object, TimeSpan.FromSeconds(6), isAudioRenditionChild: true);
            svc.Register(sibling, TimeSpan.FromSeconds(6), isAudioRenditionChild: true);
            svc.StampChildAudioSessions("parent", ["audio-failing", "audio-sibling"]);

            await svc.RemoveAsync("parent");

            failingChild.Verify(s => s.DisposeAsync(), Times.Once);
            sibling.State.Should().Be(LiveSessionState.Ended);
            parent.State.Should().Be(LiveSessionState.Ended);
            svc.ActiveSessionIds.Should().BeEmpty();
            Directory.Exists(tempDir).Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task DisposeAsync_FailingSession_StillDisposesRemainingSessions()
    {
        LiveStreamingService svc = NewService();
        Mock<ILiveSession> failing = MakeFailingSession("failing");
        LiveSession remaining = MakeSession("remaining");
        svc.Register(failing.Object, TimeSpan.FromSeconds(6));
        svc.Register(remaining, TimeSpan.FromSeconds(6));

        await svc.DisposeAsync();

        failing.Verify(s => s.DisposeAsync(), Times.Once);
        remaining.State.Should().Be(LiveSessionState.Ended);
        svc.ActiveSessionIds.Should().BeEmpty();
    }

    private static Mock<ILiveSession> MakeFailingSession(string id)
    {
        Mock<ILiveSession> session = new();
        session.SetupGet(s => s.SessionId).Returns(id);
        session.SetupGet(s => s.Segments).Returns(EmptySegments());
        session
            .Setup(s => s.DisposeAsync())
            .Returns(() =>
                ValueTask.FromException(new InvalidOperationException("dispose failed"))
            );
        return session;
    }

    private static async IAsyncEnumerable<Segment> EmptySegments()
    {
        await Task.CompletedTask;
        yield break;
    }

    private static async Task WaitForBufferAsync(
        ILiveStreamingService svc,
        string sessionId,
        int expectedCount
    )
    {
        await WaitForConditionAsync(() =>
        {
            if (!svc.TryGetRuntime(sessionId, out LiveRuntimeSession r))
                return false;
            return r.SnapshotSegments().Count >= expectedCount;
        });
    }

    private static async Task WaitForConditionAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;
            await Task.Delay(10);
        }
        condition()
            .Should()
            .BeTrue("the drainer should have processed the segments within timeout");
    }
}
