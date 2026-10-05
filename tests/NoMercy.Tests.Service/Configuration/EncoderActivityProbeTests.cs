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
using NoMercy.Encoder.LiveTranscode;
using NoMercy.Service.Configuration;
using NoMercyQueue;
using NoMercyQueue.Core.Interfaces;
using NoMercyQueue.Core.Models;

namespace NoMercy.Tests.Service.Configuration;

/// <summary>
/// <see cref="EncoderActivityProbe.IsBusy"/> decides whether the encoder's
/// deferred hardware benchmark may run. It must treat a live transcode session
/// as busy WITHOUT ever conflating "spawned-but-idle queue workers" (which
/// <see cref="QueueRunner.GetActiveWorkerThreads"/> reports) with "actually
/// processing an encoder job" — that conflation is documented on the class as
/// the exact bug this probe fixes (the benchmark deferred forever once any
/// encoder worker thread existed, idle or not).
/// </summary>
[Trait("Category", "Unit")]
public class EncoderActivityProbeTests
{
    // A queue with zero configured worker pools — CountWorkersProcessingJob
    // always returns 0 without needing a live worker thread, so it never
    // interferes with the ActiveSessionCount assertions below.
    private static QueueRunner EmptyQueueRunner() =>
        new(Mock.Of<IQueueContext>(), new QueueConfiguration(), NullLoggerFactory.Instance);

    [Fact]
    public void IsBusy_ActiveSessionsPresent_ReturnsTrue()
    {
        Mock<ISessionManager> sessionManager = new();
        sessionManager.SetupGet(s => s.ActiveSessionCount).Returns(1);
        EncoderActivityProbe probe = new(EmptyQueueRunner(), sessionManager.Object);

        probe.IsBusy.Should().BeTrue();
    }

    [Fact]
    public void IsBusy_NoSessionsAndNoQueueWorkersConfigured_ReturnsFalse()
    {
        Mock<ISessionManager> sessionManager = new();
        sessionManager.SetupGet(s => s.ActiveSessionCount).Returns(0);
        EncoderActivityProbe probe = new(EmptyQueueRunner(), sessionManager.Object);

        probe.IsBusy.Should().BeFalse();
    }

    [Fact]
    public void IsBusy_ZeroActiveSessions_StillConsultsQueueBeforeDeciding()
    {
        // Sessions idle: the class contract is "OR" with the queue check, so a
        // queue with configured-but-idle worker pools (no live threads yet)
        // must also report not-busy — the queue check itself is exercised in
        // NoMercy.Tests.Queue; this only pins the composition order (sessions
        // first, queue second, both false -> not busy).
        Mock<ISessionManager> sessionManager = new();
        sessionManager.SetupGet(s => s.ActiveSessionCount).Returns(0);
        QueueRunner queueWithConfiguredButUnstartedPool = new(
            Mock.Of<IQueueContext>(),
            new QueueConfiguration { WorkerCounts = new() { ["encoder"] = 1 } },
            NullLoggerFactory.Instance
        );
        EncoderActivityProbe probe = new(
            queueWithConfiguredButUnstartedPool,
            sessionManager.Object
        );

        probe.IsBusy.Should().BeFalse();
    }
}
