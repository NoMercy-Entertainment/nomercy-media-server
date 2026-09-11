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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NoMercy.Data.Plugins;
using NoMercy.Plugins.Abstractions;

namespace NoMercy.Tests.Repositories.Plugins;

/// <summary>
/// The one guard every plugin-facing facade runs its calls through: a failure
/// nobody planned for becomes a refusal in the caller's own words, and the
/// cancellation a caller asked for is the one thing that still reaches it.
/// </summary>
public class PluginCallGuardTests
{
    [Fact]
    public async Task AThrowingCall_BecomesARefusalNamingTheExceptionType()
    {
        Mock<ILogger> logger = new();

        PluginWriteResult result = await PluginCallGuard.RunAsync(
            "plugin 1: WriteSomething",
            () => throw new IOException("the volume went away"),
            PluginWriteResult.Refused,
            logger.Object
        );

        result.Ok.Should().BeFalse();
        result.Refusal.Should().Be("the server could not complete this call: IOException");
        logger.Verify(
            log =>
                log.Log(
                    LogLevel.Warning,
                    It.IsAny<EventId>(),
                    It.IsAny<It.IsAnyType>(),
                    It.IsAny<IOException>(),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()
                ),
            Times.Once
        );
    }

    [Fact]
    public async Task ACancelledCall_Propagates()
    {
        Func<Task<PluginWriteResult>> guarded = () =>
            PluginCallGuard.RunAsync<PluginWriteResult>(
                "plugin 1: WriteSomething",
                () => throw new OperationCanceledException(),
                PluginWriteResult.Refused,
                NullLogger.Instance
            );

        await guarded.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task AMemberWithoutARefusalChannel_FallsBackToTheSuppliedValue()
    {
        bool exists = await PluginCallGuard.RunOrAsync(
            "a plugin's ExistsAsync call",
            () => throw new IOException("the volume went away"),
            false,
            NullLogger.Instance
        );

        exists.Should().BeFalse();

        Stream? opened = await PluginCallGuard.RunOrAsync<Stream?>(
            "a plugin's OpenReadAsync call",
            () => throw new IOException("the volume went away"),
            null,
            NullLogger.Instance
        );

        opened.Should().BeNull();
    }

    [Fact]
    public async Task AVoidMember_SwallowsToANoOp()
    {
        Func<Task> guarded = () =>
            PluginCallGuard.RunAsync(
                "a plugin's TouchAsync call",
                () => throw new IOException("the volume went away"),
                NullLogger.Instance
            );

        await guarded.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ACallThatSucceeds_IsHandedBackUntouched()
    {
        PluginWriteResult result = await PluginCallGuard.RunAsync(
            "plugin 1: WriteSomething",
            () => Task.FromResult(PluginWriteResult.Accepted()),
            PluginWriteResult.Refused,
            NullLogger.Instance
        );

        result.Ok.Should().BeTrue();
    }
}
