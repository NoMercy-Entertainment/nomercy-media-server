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
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Moq;
using Newtonsoft.Json.Linq;
using NoMercy.Api.Hubs;
using NoMercy.Api.Hubs.Filters;
using NoMercy.Authorization;
using NoMercy.Database.Models.Users;
using NoMercy.NmSystem.Logging;
using NoMercy.Service;
using NoMercy.Tests.Api.Infrastructure;
using Xunit;

namespace NoMercy.Tests.Api.Hubs.Filters;

[Trait("Category", "Unit")]
public class SessionRequestLogFilterTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private const string DeviceId = "device-4f2a";
    private const string ConnectionId = "conn-91c3";
    private const string Jwt = "eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiJ0ZXN0In0.c2lnbmF0dXJlc2lnbmF0dXJl";

    private sealed class TestHttpContextFeature : IHttpContextFeature
    {
        public HttpContext? HttpContext { get; set; }
    }

    private static readonly Type[] SessionHubs =
    [
        typeof(DeviceHub),
        typeof(MusicHub),
        typeof(VideoHub),
        typeof(CastHub),
    ];

    private static (
        SessionRequestLogFilter Filter,
        CapturingLogger<SessionRequestLogFilter> Log
    ) NewFilter()
    {
        CapturingLogger<SessionRequestLogFilter> log = new();
        return (new(log, new SessionLogPolicy(), KnownUsers()), log);
    }

    private const string UserName = "Test Owner";

    private static IUserCache KnownUsers()
    {
        Mock<IUserCache> users = new();
        users.Setup(u => u.GetUser(UserId)).Returns(new User { Id = UserId, Name = UserName });
        return users.Object;
    }

    [Fact]
    public async Task InvokeMethodAsync_KnownUser_LogsTheUserNameWithTheId()
    {
        (SessionRequestLogFilter filter, CapturingLogger<SessionRequestLogFilter> log) =
            NewFilter();

        await filter.InvokeMethodAsync(Invocation(typeof(CastHub), nameof(CastHub.Stop)), Ok);

        log.Entries[0].Message.Should().Contain($"user={UserName} ({UserId})");
    }

    [Fact]
    public async Task InvokeMethodAsync_UnknownUser_LogsTheIdAlone()
    {
        CapturingLogger<SessionRequestLogFilter> log = new();
        SessionRequestLogFilter filter = new(log, new SessionLogPolicy(), Mock.Of<IUserCache>());

        await filter.InvokeMethodAsync(Invocation(typeof(CastHub), nameof(CastHub.Stop)), Ok);

        log.Entries[0].Message.Should().Contain($"user={UserId} ");
    }

    [Fact]
    public async Task OnConnectedAsync_KnownUser_LogsTheUserNameWithTheId()
    {
        (SessionRequestLogFilter filter, CapturingLogger<SessionRequestLogFilter> log) =
            NewFilter();

        await filter.OnConnectedAsync(
            new HubLifetimeContext(Caller(), Mock.Of<IServiceProvider>(), HubOf(typeof(VideoHub))),
            _ => Task.CompletedTask
        );

        log.Entries[0].Message.Should().Contain($"user={UserName} ({UserId})");
    }

    private static HubCallerContext Caller()
    {
        DefaultHttpContext http = new();
        http.Request.QueryString = new($"?client_id={DeviceId}&access_token={Jwt}");
        FeatureCollection features = new();
        features.Set<IHttpContextFeature>(new TestHttpContextFeature { HttpContext = http });

        Mock<HubCallerContext> caller = new();
        caller.Setup(c => c.ConnectionId).Returns(ConnectionId);
        caller.Setup(c => c.Features).Returns(features);
        caller
            .Setup(c => c.User)
            .Returns(
                new ClaimsPrincipal(
                    new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, UserId.ToString())],
                        "test"
                    )
                )
            );
        return caller.Object;
    }

    private static Hub HubOf(Type hubType) => (Hub)RuntimeHelpers.GetUninitializedObject(hubType);

    private static HubInvocationContext Invocation(
        Type hubType,
        string method,
        params object?[] arguments
    ) => Invocation(hubType, hubType.GetMethod(method)!, arguments);

    private static HubInvocationContext Invocation(
        Type hubType,
        MethodInfo method,
        object?[] arguments
    ) => new(Caller(), Mock.Of<IServiceProvider>(), HubOf(hubType), method, arguments);

    private static ValueTask<object?> Ok(HubInvocationContext _) => new(HubCommandResult.Success());

    [Theory]
    [InlineData(typeof(DeviceHub), nameof(DeviceHub.WakeForMusic))]
    [InlineData(typeof(MusicHub), nameof(MusicHub.ChangeDeviceCommand))]
    [InlineData(typeof(VideoHub), nameof(VideoHub.ChangeDeviceCommand))]
    [InlineData(typeof(CastHub), nameof(CastHub.Launch))]
    public async Task InvokeMethodAsync_SessionHubCall_WritesRequestAndResultLine(
        Type hubType,
        string method
    )
    {
        (SessionRequestLogFilter filter, CapturingLogger<SessionRequestLogFilter> log) =
            NewFilter();
        object?[] arguments =
            hubType.GetMethod(method)!.GetParameters().Length == 0
                ? []
                : [Ulid.NewUlid().ToString()];

        await filter.InvokeMethodAsync(Invocation(hubType, method, arguments), Ok);

        log.Entries.Should().HaveCount(2);
        log.Entries[0].Level.Should().Be(LogLevel.Information);
        log.Entries[0]
            .Message.Should()
            .Contain($"{hubType.Name}.{method}")
            .And.Contain($"user={UserName} ({UserId})")
            .And.Contain($"device={DeviceId}")
            .And.Contain($"connection={ConnectionId}");
        log.Entries[1].Level.Should().Be(LogLevel.Information);
        log.Entries[1]
            .Message.Should()
            .Contain($"{hubType.Name}.{method}")
            .And.Contain("ok")
            .And.MatchRegex(@"in \d+ms");
    }

    [Fact]
    public async Task InvokeMethodAsync_RefusedResult_LogsWarningWithReason()
    {
        (SessionRequestLogFilter filter, CapturingLogger<SessionRequestLogFilter> log) =
            NewFilter();

        await filter.InvokeMethodAsync(
            Invocation(typeof(VideoHub), nameof(VideoHub.ChangeDeviceCommand), "x"),
            _ => new(HubCommandResult.NotFound("Device was not found for this user."))
        );

        log.Entries[1].Level.Should().Be(LogLevel.Warning);
        log.Entries[1].Message.Should().Contain("refused").And.Contain("not_found");
    }

    [Fact]
    public async Task InvokeMethodAsync_FailedResult_LogsError()
    {
        (SessionRequestLogFilter filter, CapturingLogger<SessionRequestLogFilter> log) =
            NewFilter();

        await filter.InvokeMethodAsync(
            Invocation(typeof(DeviceHub), nameof(DeviceHub.WakeForVideo), "x"),
            _ => new(HubCommandResult.Failed())
        );

        log.Entries[1].Level.Should().Be(LogLevel.Error);
        log.Entries[1].Message.Should().Contain("failed").And.Contain("operation_failed");
    }

    [Fact]
    public async Task InvokeMethodAsync_ThrowingHubMethod_LogsExceptionTypeAndRethrows()
    {
        (SessionRequestLogFilter filter, CapturingLogger<SessionRequestLogFilter> log) =
            NewFilter();

        Func<Task> act = async () =>
            await filter.InvokeMethodAsync(
                Invocation(typeof(MusicHub), nameof(MusicHub.ChangeDeviceCommand), "x"),
                _ => throw new TimeoutException("slow")
            );

        await act.Should().ThrowAsync<TimeoutException>();
        log.Entries[1].Level.Should().Be(LogLevel.Error);
        log.Entries[1].Message.Should().Contain("TimeoutException");
    }

    [Theory]
    [InlineData(typeof(VideoHub), nameof(VideoHub.SetTime))]
    [InlineData(typeof(MusicHub), nameof(MusicHub.CurrentTimeCommand))]
    [InlineData(typeof(MusicHub), nameof(MusicHub.ReportPositionCommand))]
    [InlineData(typeof(CastHub), nameof(CastHub.Time))]
    public async Task InvokeMethodAsync_TimeReport_LogsDebugAndNeverInformation(
        Type hubType,
        string method
    )
    {
        (SessionRequestLogFilter filter, CapturingLogger<SessionRequestLogFilter> log) =
            NewFilter();
        object?[] arguments = hubType
            .GetMethod(method)!
            .GetParameters()
            .Select(_ => (object?)null)
            .ToArray();

        await filter.InvokeMethodAsync(Invocation(hubType, method, arguments), Ok);

        log.Entries.Should().HaveCount(2);
        log.Entries.Should().OnlyContain(entry => entry.Level == LogLevel.Debug);
    }

    [Fact]
    public async Task InvokeMethodAsync_TokenInArguments_NeverAppearsInAnyLine()
    {
        (SessionRequestLogFilter filter, CapturingLogger<SessionRequestLogFilter> log) =
            NewFilter();
        JObject data = new()
        {
            ["position"] = 42,
            ["access_token"] = "SECRET-IN-OBJECT",
            ["authorization"] = "Bearer SECRET-IN-HEADER",
        };

        await filter.InvokeMethodAsync(
            Invocation(typeof(VideoHub), nameof(VideoHub.PlaybackCommand), Jwt, data),
            Ok
        );
        await filter.InvokeMethodAsync(
            Invocation(typeof(DeviceHub), nameof(DeviceHub.WakeForMusic), "Bearer SECRET-AS-ID"),
            Ok
        );
        await filter.InvokeMethodAsync(
            Invocation(typeof(CastHub), nameof(CastHub.SelectChromecast), "Living Room TV"),
            Ok
        );

        string all = string.Join('\n', log.Entries.Select(entry => entry.Message));
        all.Should().Contain("position=42");
        all.Should().NotContain("eyJ").And.NotContain("SECRET").And.NotContain("Living Room");
    }

    [Fact]
    public async Task InvokeMethodAsync_QueryStringToken_NeverAppearsInAnyLine()
    {
        (SessionRequestLogFilter filter, CapturingLogger<SessionRequestLogFilter> log) =
            NewFilter();

        await filter.InvokeMethodAsync(Invocation(typeof(CastHub), nameof(CastHub.Stop)), Ok);

        string.Join('\n', log.Entries.Select(entry => entry.Message)).Should().NotContain(Jwt);
    }

    [Fact]
    public async Task InvokeMethodAsync_NonSessionHub_WritesNothing()
    {
        (SessionRequestLogFilter filter, CapturingLogger<SessionRequestLogFilter> log) =
            NewFilter();

        await filter.InvokeMethodAsync(
            Invocation(
                typeof(PluginHub),
                nameof(PluginHub.Send),
                Ulid.NewUlid().ToString(),
                "ping",
                null
            ),
            Ok
        );

        log.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task InvokeMethodAsync_EveryPublicMethodOfTheFourHubs_WritesRequestAndResultLine()
    {
        (SessionRequestLogFilter filter, CapturingLogger<SessionRequestLogFilter> log) =
            NewFilter();
        List<(Type Hub, MethodInfo Method)> methods = SessionHubs
            .SelectMany(hub =>
                hub.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(m =>
                        !m.IsSpecialName
                        && m.GetBaseDefinition().DeclaringType != typeof(object)
                        && m.GetBaseDefinition().DeclaringType != typeof(Hub)
                    )
                    .Select(m => (hub, m))
            )
            .ToList();

        foreach ((Type hub, MethodInfo method) in methods)
            await filter.InvokeMethodAsync(
                Invocation(
                    hub,
                    method,
                    method.GetParameters().Select(_ => (object?)null).ToArray()
                ),
                Ok
            );

        methods.Should().HaveCountGreaterThan(40);
        log.Entries.Should().HaveCount(methods.Count * 2);
        foreach ((Type hub, MethodInfo method) in methods)
            log.Entries.Should()
                .Contain(entry => entry.Message.Contains($"{hub.Name}.{method.Name} "));
    }

    [Fact]
    public async Task OnConnectedAsync_SessionHub_LogsDeviceConnection()
    {
        (SessionRequestLogFilter filter, CapturingLogger<SessionRequestLogFilter> log) =
            NewFilter();

        await filter.OnConnectedAsync(
            new HubLifetimeContext(Caller(), Mock.Of<IServiceProvider>(), HubOf(typeof(VideoHub))),
            _ => Task.CompletedTask
        );

        log.Entries.Should().ContainSingle();
        log.Entries[0].Level.Should().Be(LogLevel.Information);
        log.Entries[0]
            .Message.Should()
            .Contain("connected")
            .And.Contain("VideoHub")
            .And.Contain($"user={UserName} ({UserId})")
            .And.Contain($"device={DeviceId}")
            .And.NotContain(Jwt);
    }

    [Fact]
    public async Task OnDisconnectedAsync_WithException_LogsWarningWithReason()
    {
        (SessionRequestLogFilter filter, CapturingLogger<SessionRequestLogFilter> log) =
            NewFilter();

        await filter.OnDisconnectedAsync(
            new HubLifetimeContext(Caller(), Mock.Of<IServiceProvider>(), HubOf(typeof(MusicHub))),
            new TimeoutException("no keep-alive"),
            (_, _) => Task.CompletedTask
        );

        log.Entries.Should().ContainSingle();
        log.Entries[0].Level.Should().Be(LogLevel.Warning);
        log.Entries[0].Message.Should().Contain("disconnected").And.Contain("TimeoutException");
    }

    [Fact]
    public async Task OnDisconnectedAsync_CleanClose_LogsInformationWithClosedReason()
    {
        (SessionRequestLogFilter filter, CapturingLogger<SessionRequestLogFilter> log) =
            NewFilter();

        await filter.OnDisconnectedAsync(
            new HubLifetimeContext(Caller(), Mock.Of<IServiceProvider>(), HubOf(typeof(CastHub))),
            null,
            (_, _) => Task.CompletedTask
        );

        log.Entries[0].Level.Should().Be(LogLevel.Information);
        log.Entries[0].Message.Should().Contain("reason=closed");
    }

    private static (SessionRequestLogFilter Filter, StringWriter Output) FilterOnRealLogger(
        Dictionary<string, string> environment
    )
    {
        StringWriter output = new();
        NoMercyLoggerOptions options = new() { Color = false };
        options.ApplyEnvironment(name => environment.GetValueOrDefault(name));
        NoMercyLoggerProvider provider = new(options, output);
        return (
            new(
                new CustomLogger<SessionRequestLogFilter>(provider),
                new SessionLogPolicy(),
                KnownUsers()
            ),
            output
        );
    }

    [Fact]
    public async Task InvokeMethodAsync_DefaultLevels_WritesRequestButNotTimeReport()
    {
        (SessionRequestLogFilter filter, StringWriter output) = FilterOnRealLogger([]);

        await filter.InvokeMethodAsync(Invocation(typeof(CastHub), nameof(CastHub.Stop)), Ok);
        await filter.InvokeMethodAsync(
            Invocation(typeof(VideoHub), nameof(VideoHub.SetTime), new object?[] { null }),
            Ok
        );

        output.ToString().Should().Contain("CastHub.Stop").And.NotContain("SetTime");
    }

    [Fact]
    public async Task InvokeMethodAsync_SessionLevelSetToDebug_WritesTimeReport()
    {
        (SessionRequestLogFilter filter, StringWriter output) = FilterOnRealLogger(
            new() { ["NOMERCY_LOG_LEVEL_SESSION"] = "Debug" }
        );

        await filter.InvokeMethodAsync(
            Invocation(typeof(VideoHub), nameof(VideoHub.SetTime), new object?[] { null }),
            Ok
        );

        output.ToString().Should().Contain("VideoHub.SetTime");
    }

    [Fact]
    public async Task InvokeMethodAsync_SessionLevelSetToWarning_DropsInformationRequestLines()
    {
        (SessionRequestLogFilter filter, StringWriter output) = FilterOnRealLogger(
            new() { ["NOMERCY_LOG_LEVEL_SESSION"] = "Warning" }
        );

        await filter.InvokeMethodAsync(Invocation(typeof(CastHub), nameof(CastHub.Stop)), Ok);
        await filter.InvokeMethodAsync(
            Invocation(typeof(CastHub), nameof(CastHub.Stop)),
            _ => new(HubCommandResult.Forbidden("no"))
        );

        string text = output.ToString();
        text.Should().Contain("refused");
        text.Should().NotContain("request");
    }
}
