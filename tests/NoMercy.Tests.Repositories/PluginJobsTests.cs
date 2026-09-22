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

using FluentAssertions;
using Moq;
using NoMercy.Data.Plugins;
using NoMercy.PluginSdk.Abstractions;
using NoMercyQueue.Core.Interfaces;
using NoMercyQueue.Core.Models;
using Xunit;

namespace NoMercy.Tests.Repositories;

/// <summary>
/// What a plugin is told about work it asked for.
///
/// <para>
/// The distinction under test is the one the whole facade exists for: a plugin
/// holding a downloaded file deletes it when the encode lands. Deleting on a
/// failure loses the episode; never deleting leaves the owner two copies of
/// everything, re-checked on every start. Before this, both read as "the library
/// does not have it yet", for ever.
/// </para>
/// </summary>
public class PluginJobsTests
{
    private const string Handle = "b7d2f1a0";

    private static PluginJobs Jobs(QueueJobModel? queued, FailedJobModel? failed)
    {
        Mock<IQueueContext> queue = new(MockBehavior.Strict);

        queue.Setup(context => context.FindJobByPayloadHash(Handle)).Returns(queued);
        queue.Setup(context => context.FindFailedJobByPayloadHash(Handle)).Returns(failed);

        return new(queue.Object);
    }

    private static QueueJobModel Job(DateTime? reservedAt)
    {
        return new()
        {
            Id = 41,
            Queue = "encoder",
            Payload = "{}",
            ReservedAt = reservedAt,
            AvailableAt = DateTime.UtcNow,
        };
    }

    [Fact]
    public async Task AJobNobodyHasTakenYetIsWaiting()
    {
        PluginJobStatus? status = await Jobs(Job(reservedAt: null), null).StatusAsync(Handle);

        status!.State.Should().Be(PluginJobState.Queued);
        status.Settled.Should().BeFalse();
    }

    // Reserved means a worker has it. The difference from Queued is what tells
    // an owner "nothing is happening yet" from "it is happening now".
    [Fact]
    public async Task AJobAWorkerHasTakenIsRunning()
    {
        PluginJobStatus? status = await Jobs(Job(DateTime.UtcNow), null).StatusAsync(Handle);

        status!.State.Should().Be(PluginJobState.Running);
        status.Settled.Should().BeFalse();
    }

    [Fact]
    public async Task AFailedJobSaysSoAndSaysWhy()
    {
        DateTime failedAt = new(2026, 8, 24, 22, 33, 0, DateTimeKind.Utc);

        FailedJobModel failure = new()
        {
            Queue = "encoder",
            Payload = "{}",
            Exception = "ffmpeg exited with 1: no such file or directory",
            FailedAt = failedAt,
        };

        PluginJobStatus? status = await Jobs(null, failure).StatusAsync(Handle);

        status!.State.Should().Be(PluginJobState.Failed);
        status.Settled.Should().BeTrue();
        status.Failure.Should().Contain("ffmpeg exited with 1");
        status.FinishedAt.Should().Be(failedAt);
    }

    /// <summary>
    /// Gone from both tables. The work was queued - a plugin only ever holds an
    /// id this server handed it - so it ran and its row was cleared.
    /// </summary>
    [Fact]
    public async Task AJobInNeitherTableFinished()
    {
        PluginJobStatus? status = await Jobs(null, null).StatusAsync(Handle);

        status!.State.Should().Be(PluginJobState.Finished);
        status.Settled.Should().BeTrue();
        status.Failure.Should().BeNull();
    }

    /// <summary>
    /// The two outcomes a caller must never confuse, asked the same way.
    ///
    /// <para>
    /// Both are settled, and only one carries a reason. A facade that returned
    /// the same answer for both would be the state this replaced, where the
    /// owner waited for an episode that was never coming and no line anywhere
    /// said why.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AFailureAndACompletionAreNotTheSameAnswer()
    {
        FailedJobModel failure = new()
        {
            Queue = "encoder",
            Payload = "{}",
            Exception = "out of disk",
        };

        PluginJobStatus? failed = await Jobs(null, failure).StatusAsync(Handle);
        PluginJobStatus? finished = await Jobs(null, null).StatusAsync(Handle);

        failed!.State.Should().NotBe(finished!.State);
        failed.Failure.Should().NotBeNull();
        finished.Failure.Should().BeNull();
    }
}
