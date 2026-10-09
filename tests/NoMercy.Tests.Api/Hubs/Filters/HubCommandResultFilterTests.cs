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

using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NoMercy.Api.Hubs;
using NoMercy.Api.Hubs.Filters;
using Xunit;

namespace NoMercy.Tests.Api.Hubs.Filters;

[Trait("Category", "Unit")]
public class HubCommandResultFilterTests
{
    private static readonly MethodInfo SendMethod = typeof(PluginHub).GetMethod(
        nameof(PluginHub.Send)
    )!;

    [Fact]
    public async Task InvalidPluginId_ReturnsResultWithoutInvokingPlugin()
    {
        HubCommandResultFilter filter = NewFilter();
        HubInvocationContext invocation = Invocation("bad-id", "ping");

        object? result = await filter.InvokeMethodAsync(
            invocation,
            _ => throw new InvalidOperationException("Plugin must not run")
        );

        HubCommandResult commandResult = Assert.IsType<HubCommandResult>(result);
        Assert.Equal("invalid_input", commandResult.ErrorCode);
    }

    [Fact]
    public async Task ValidPluginCall_KeepsLegacyBooleanSuccess()
    {
        HubCommandResultFilter filter = NewFilter();
        HubInvocationContext invocation = Invocation(Ulid.NewUlid().ToString(), "ping");

        object? result = await filter.InvokeMethodAsync(invocation, _ => new(true));

        Assert.Equal(true, result);
    }

    [Fact]
    public async Task UnavailablePlugin_ReturnsNotFoundResult()
    {
        HubCommandResultFilter filter = NewFilter();
        HubInvocationContext invocation = Invocation(Ulid.NewUlid().ToString(), "ping");

        object? result = await filter.InvokeMethodAsync(invocation, _ => new(false));

        HubCommandResult commandResult = Assert.IsType<HubCommandResult>(result);
        Assert.Equal("not_found", commandResult.ErrorCode);
    }

    [Fact]
    public async Task ThrowingHubCall_ReturnsOperationFailedResult()
    {
        HubCommandResultFilter filter = NewFilter();
        HubInvocationContext invocation = Invocation(Ulid.NewUlid().ToString(), "ping");

        object? result = await filter.InvokeMethodAsync(
            invocation,
            _ => throw new InvalidOperationException("private detail")
        );

        HubCommandResult commandResult = Assert.IsType<HubCommandResult>(result);
        Assert.Equal("operation_failed", commandResult.ErrorCode);
        Assert.DoesNotContain("private detail", commandResult.Message);
    }

    private static HubCommandResultFilter NewFilter() =>
        new(NullLogger<HubCommandResultFilter>.Instance);

    private static HubInvocationContext Invocation(string pluginId, string method)
    {
        Mock<HubCallerContext> caller = new();
        caller.Setup(context => context.ConnectionId).Returns("test-connection");
        PluginHub hub = (PluginHub)RuntimeHelpers.GetUninitializedObject(typeof(PluginHub));

        return new HubInvocationContext(
            caller.Object,
            Mock.Of<IServiceProvider>(),
            hub,
            SendMethod,
            [pluginId, method, null]
        );
    }
}
