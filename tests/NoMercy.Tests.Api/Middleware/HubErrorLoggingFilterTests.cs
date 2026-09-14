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
using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NoMercy.Api.Middleware;
using NoMercy.Authorization;
using NoMercy.Database.Models.Users;
using NoMercy.NmSystem.Information;
using Xunit;

namespace NoMercy.Tests.Api.Middleware;

// HubErrorLoggingFilter is the single choke point every SignalR hub method call
// passes through. Its job is: (1) let calls from connections it can't identify
// through untouched, (2) run identified calls under SQLite-retry protection,
// and (3) translate every failure mode a client can trigger (missing method,
// bad arguments, arbitrary exception) into a HubException whose message is
// verbose in dev and generic in production — never leak internals to a
// self-hosted user's browser console in prod, never hide them from the boss
// running --dev.
[Trait("Category", "Middleware")]
public sealed class HubErrorLoggingFilterTests
{
    private sealed class FakeHub : Hub
    {
        public Task Ping() => Task.CompletedTask;
    }

    private static readonly MethodInfo PingMethod = typeof(FakeHub).GetMethod(
        nameof(FakeHub.Ping)
    )!;

    private static HubInvocationContext CreateInvocation(
        ClaimsPrincipal? user,
        IReadOnlyList<object?>? arguments = null
    )
    {
        Mock<HubCallerContext> context = new();
        context.Setup(c => c.User).Returns(user);
        context.Setup(c => c.ConnectionId).Returns("test-connection");

        return new HubInvocationContext(
            context.Object,
            Mock.Of<IServiceProvider>(),
            new FakeHub(),
            PingMethod,
            arguments ?? []
        );
    }

    private static ClaimsPrincipal PrincipalFor(Guid userId)
    {
        return new(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, userId.ToString())]));
    }

    private static HubErrorLoggingFilter CreateFilter()
    {
        return new(NullLogger<HubErrorLoggingFilter>.Instance, UserCache.Current);
    }

    [Fact]
    public async Task InvokeMethodAsync_NullUser_PassesThroughWithoutTouchingUserCache()
    {
        HubErrorLoggingFilter filter = CreateFilter();
        HubInvocationContext invocation = CreateInvocation(user: null);

        object? result = await filter.InvokeMethodAsync(invocation, _ => new("passthrough"));

        result.Should().Be("passthrough");
    }

    [Fact]
    public async Task InvokeMethodAsync_PrincipalWithoutNameIdentifier_PassesThrough()
    {
        HubErrorLoggingFilter filter = CreateFilter();
        ClaimsPrincipal claimless = new(new ClaimsIdentity([new("some-other-claim", "value")]));
        HubInvocationContext invocation = CreateInvocation(claimless);

        object? result = await filter.InvokeMethodAsync(invocation, _ => new("passthrough"));

        result.Should().Be("passthrough");
    }

    [Fact]
    public async Task InvokeMethodAsync_MalformedGuidClaim_PassesThrough()
    {
        HubErrorLoggingFilter filter = CreateFilter();
        ClaimsPrincipal malformed = new(
            new ClaimsIdentity([new(ClaimTypes.NameIdentifier, "not-a-guid")])
        );
        HubInvocationContext invocation = CreateInvocation(malformed);

        object? result = await filter.InvokeMethodAsync(invocation, _ => new("passthrough"));

        result.Should().Be("passthrough");
    }

    [Fact]
    public async Task InvokeMethodAsync_ValidGuidNotInUserCache_PassesThrough()
    {
        HubErrorLoggingFilter filter = CreateFilter();
        // A freshly minted GUID is astronomically unlikely to already be a
        // seeded UserCache.Current entry left behind by another test class in
        // this sequential (DisableTestParallelization) assembly.
        HubInvocationContext invocation = CreateInvocation(PrincipalFor(Guid.NewGuid()));

        object? result = await filter.InvokeMethodAsync(invocation, _ => new("passthrough"));

        result.Should().Be("passthrough");
    }

    [Fact]
    public async Task InvokeMethodAsync_KnownUser_ReturnsInnerResult()
    {
        HubErrorLoggingFilter filter = CreateFilter();
        User user = new()
        {
            Id = Guid.NewGuid(),
            Name = "Filter Test User",
            Email = "filter-test@nomercy.tv",
        };
        UserCache.Current.AddUser(user);
        try
        {
            HubInvocationContext invocation = CreateInvocation(PrincipalFor(user.Id));

            object? result = await filter.InvokeMethodAsync(invocation, _ => new("ok"));

            result.Should().Be("ok");
        }
        finally
        {
            UserCache.Current.RemoveUser(user);
        }
    }

    [Fact]
    public async Task InvokeMethodAsync_KnownUser_HubExceptionFromNext_IsRethrownUnchanged()
    {
        HubErrorLoggingFilter filter = CreateFilter();
        User user = new()
        {
            Id = Guid.NewGuid(),
            Name = "Filter Test User",
            Email = "filter-test@nomercy.tv",
        };
        UserCache.Current.AddUser(user);
        try
        {
            HubInvocationContext invocation = CreateInvocation(PrincipalFor(user.Id));

            Func<Task> act = async () =>
                await filter.InvokeMethodAsync(
                    invocation,
                    _ => throw new HubException("client-facing failure")
                );

            (await act.Should().ThrowAsync<HubException>()).WithMessage("client-facing failure");
        }
        finally
        {
            UserCache.Current.RemoveUser(user);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InvokeMethodAsync_MethodDoesNotExist_MessageHonorsIsDev(bool isDev)
    {
        bool original = Config.IsDev;
        Config.IsDev = isDev;
        HubErrorLoggingFilter filter = CreateFilter();
        User user = new()
        {
            Id = Guid.NewGuid(),
            Name = "Filter Test User",
            Email = "filter-test@nomercy.tv",
        };
        UserCache.Current.AddUser(user);
        try
        {
            HubInvocationContext invocation = CreateInvocation(PrincipalFor(user.Id));

            Func<Task> act = async () =>
                await filter.InvokeMethodAsync(
                    invocation,
                    _ => throw new InvalidOperationException("Method 'Ping' does not exist.")
                );

            HubException thrown = (await act.Should().ThrowAsync<HubException>()).Which;
            if (isDev)
                thrown.Message.Should().Be("Method 'Ping' does not exist on hub 'FakeHub'");
            else
                thrown.Message.Should().Be("An internal error occurred");
        }
        finally
        {
            UserCache.Current.RemoveUser(user);
            Config.IsDev = original;
        }
    }

    [Fact]
    public async Task InvokeMethodAsync_UnrelatedInvalidOperationException_IsWrappedAsHubException()
    {
        // "does not exist" is a message substring match, not an exception
        // sub-type check -- any other InvalidOperationException must still be
        // caught by the final catch-all and wrapped, not bubble up raw.
        HubErrorLoggingFilter filter = CreateFilter();
        User user = new()
        {
            Id = Guid.NewGuid(),
            Name = "Filter Test User",
            Email = "filter-test@nomercy.tv",
        };
        UserCache.Current.AddUser(user);
        try
        {
            HubInvocationContext invocation = CreateInvocation(PrincipalFor(user.Id));

            Func<Task> act = async () =>
                await filter.InvokeMethodAsync(
                    invocation,
                    _ => throw new InvalidOperationException("unrelated failure")
                );

            await act.Should().ThrowAsync<HubException>();
        }
        finally
        {
            UserCache.Current.RemoveUser(user);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InvokeMethodAsync_ArgumentException_MessageHonorsIsDev(bool isDev)
    {
        bool original = Config.IsDev;
        Config.IsDev = isDev;
        HubErrorLoggingFilter filter = CreateFilter();
        User user = new()
        {
            Id = Guid.NewGuid(),
            Name = "Filter Test User",
            Email = "filter-test@nomercy.tv",
        };
        UserCache.Current.AddUser(user);
        try
        {
            HubInvocationContext invocation = CreateInvocation(
                PrincipalFor(user.Id),
                arguments: ["wrong-type"]
            );

            Func<Task> act = async () =>
                await filter.InvokeMethodAsync(
                    invocation,
                    _ => throw new ArgumentException("expected int, got string")
                );

            HubException thrown = (await act.Should().ThrowAsync<HubException>()).Which;
            if (isDev)
                thrown
                    .Message.Should()
                    .Be("Invalid arguments for method 'Ping': expected int, got string");
            else
                thrown.Message.Should().Be("An internal error occurred");
        }
        finally
        {
            UserCache.Current.RemoveUser(user);
            Config.IsDev = original;
        }
    }

    [Fact]
    public async Task InvokeMethodAsync_ArgumentException_WithNoArguments_StillWraps()
    {
        HubErrorLoggingFilter filter = CreateFilter();
        User user = new()
        {
            Id = Guid.NewGuid(),
            Name = "Filter Test User",
            Email = "filter-test@nomercy.tv",
        };
        UserCache.Current.AddUser(user);
        try
        {
            HubInvocationContext invocation = CreateInvocation(
                PrincipalFor(user.Id),
                arguments: []
            );

            Func<Task> act = async () =>
                await filter.InvokeMethodAsync(
                    invocation,
                    _ => throw new ArgumentException("missing required argument")
                );

            await act.Should().ThrowAsync<HubException>();
        }
        finally
        {
            UserCache.Current.RemoveUser(user);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InvokeMethodAsync_UnhandledException_MessageHonorsIsDev(bool isDev)
    {
        bool original = Config.IsDev;
        Config.IsDev = isDev;
        HubErrorLoggingFilter filter = CreateFilter();
        User user = new()
        {
            Id = Guid.NewGuid(),
            Name = "Filter Test User",
            Email = "filter-test@nomercy.tv",
        };
        UserCache.Current.AddUser(user);
        try
        {
            HubInvocationContext invocation = CreateInvocation(
                PrincipalFor(user.Id),
                arguments: [1, "two"]
            );

            Func<Task> act = async () =>
                await filter.InvokeMethodAsync(
                    invocation,
                    _ => throw new InvalidCastException("boom")
                );

            HubException thrown = (await act.Should().ThrowAsync<HubException>()).Which;
            if (isDev)
                thrown.Message.Should().Be("An error occurred calling 'Ping': boom");
            else
                thrown.Message.Should().Be("An internal error occurred");
        }
        finally
        {
            UserCache.Current.RemoveUser(user);
            Config.IsDev = original;
        }
    }
}
