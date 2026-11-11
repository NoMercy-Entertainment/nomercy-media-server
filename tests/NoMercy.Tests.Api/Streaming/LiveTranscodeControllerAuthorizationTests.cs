// -----------------------------------------------------------------------------
//  Copyright (c) 2024-present NoMercy Entertainment. All rights reserved.
//
//  SPDX-License-Identifier: LicenseRef-NoMercy-Proprietary
// -----------------------------------------------------------------------------

using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NoMercy.Api.Controllers.V1.Streaming;
using NoMercy.Api.Controllers.V1.Streaming.Dtos;
using NoMercy.Api.Services;
using NoMercy.Authorization;
using NoMercy.Encoder.LiveTranscode;
using Xunit;

namespace NoMercy.Tests.Api.Streaming;

[Trait("Category", "Authorization")]
public sealed class LiveTranscodeControllerAuthorizationTests
{
    private static readonly Guid ServerOwner = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SessionUser = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Manager = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid Moderator = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private const string SessionId = "someone-elses-session";

    private static LiveSessionDto Session =>
        new(
            SessionId,
            "Transcoding",
            "1080p",
            "1080p",
            1920,
            1080,
            8000,
            0,
            0,
            0,
            false,
            DateTime.UtcNow
        );

    private static (LiveTranscodeController Controller, Mock<ILiveTranscodeService> Service) Build(
        Guid caller,
        Guid? sessionOwner = null
    )
    {
        Mock<ILiveTranscodeService> service = new();
        service.Setup(s => s.ListSessions(It.IsAny<Guid>(), true)).Returns([Session]);
        service.Setup(s => s.ListSessions(It.IsAny<Guid>(), false)).Returns([Session]);
        service
            .Setup(s =>
                s.ReportPosition(It.IsAny<Guid>(), SessionId, It.IsAny<ReportPositionRequest>())
            )
            .Returns(LiveResult.Ok(null));
        service
            .Setup(s =>
                s.ReportBufferHealth(
                    It.IsAny<Guid>(),
                    SessionId,
                    It.IsAny<ReportBufferHealthRequest>()
                )
            )
            .Returns(LiveResult.Ok(null));
        service
            .Setup(s =>
                s.ChangeQualityAsync(
                    It.IsAny<Guid>(),
                    SessionId,
                    It.IsAny<ChangeQualityRequest>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(LiveResult.Ok(null));
        service
            .Setup(s =>
                s.SeekAsync(
                    It.IsAny<Guid>(),
                    SessionId,
                    It.IsAny<SeekRequest>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(LiveResult.Ok(null));
        service
            .Setup(s =>
                s.EndSessionAsync(It.IsAny<Guid>(), SessionId, It.IsAny<CancellationToken>())
            )
            .ReturnsAsync(LiveResult.Ok(null));

        Mock<ISessionManager> sessions = new();
        sessions
            .Setup(s => s.GetOwnerUserId(SessionId))
            .Returns((sessionOwner ?? SessionUser).ToString());

        Mock<IMediaAuthorizationPolicy> policy = new();
        policy.Setup(p => p.IsOwner(It.IsAny<ClaimsPrincipal>())).Returns(caller == ServerOwner);
        policy
            .Setup(p => p.IsModerator(It.IsAny<ClaimsPrincipal>()))
            .Returns(caller == ServerOwner || caller == Manager || caller == Moderator);

        ServiceProvider provider = new ServiceCollection()
            .AddSingleton(policy.Object)
            .AddSingleton(sessions.Object)
            .BuildServiceProvider();
        DefaultHttpContext context = new()
        {
            RequestServices = provider,
            User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, caller.ToString())],
                    "test"
                )
            ),
        };
        LiveTranscodeController controller = new(service.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = context },
        };
        return (controller, service);
    }

    [Theory]
    [InlineData("manager")]
    [InlineData("moderator")]
    public void ListSessions_RefusesOtherUsersSession(string role)
    {
        Guid caller = role == "manager" ? Manager : Moderator;
        (LiveTranscodeController controller, _) = Build(caller);

        IActionResult result = controller.ListSessions();

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(403);
    }

    [Fact]
    public void ListSessions_AllowsServerOwner()
    {
        (LiveTranscodeController controller, _) = Build(ServerOwner);

        controller
            .ListSessions()
            .Should()
            .BeOfType<OkObjectResult>()
            .Which.Value.Should()
            .BeAssignableTo<IReadOnlyList<LiveSessionDto>>()
            .Which.Should()
            .ContainSingle(s => s.SessionId == SessionId);
    }

    [Fact]
    public void ListSessions_AllowsSessionUser()
    {
        (LiveTranscodeController controller, _) = Build(SessionUser);

        controller
            .ListSessions()
            .Should()
            .BeOfType<OkObjectResult>()
            .Which.Value.Should()
            .BeAssignableTo<IReadOnlyList<LiveSessionDto>>()
            .Which.Should()
            .ContainSingle(s => s.SessionId == SessionId);
    }

    [Fact]
    public async Task Manager_CanListAndControlOwnSession()
    {
        (LiveTranscodeController controller, Mock<ILiveTranscodeService> service) = Build(
            Manager,
            sessionOwner: Manager
        );

        controller
            .ListSessions()
            .Should()
            .BeOfType<OkObjectResult>()
            .Which.Value.Should()
            .BeAssignableTo<IReadOnlyList<LiveSessionDto>>()
            .Which.Should()
            .ContainSingle(s => s.SessionId == SessionId);
        (await controller.ChangeQuality(SessionId, new ChangeQualityRequest("720p")))
            .Should()
            .BeOfType<OkObjectResult>();
        service.Verify(
            s =>
                s.ChangeQualityAsync(
                    Manager,
                    SessionId,
                    It.IsAny<ChangeQualityRequest>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Theory]
    [InlineData("manager", "position")]
    [InlineData("manager", "buffer")]
    [InlineData("manager", "quality")]
    [InlineData("manager", "seek")]
    [InlineData("manager", "end")]
    [InlineData("moderator", "position")]
    [InlineData("moderator", "buffer")]
    [InlineData("moderator", "quality")]
    [InlineData("moderator", "seek")]
    [InlineData("moderator", "end")]
    public async Task Control_RefusesOtherUsersSession(string role, string action)
    {
        Guid caller = role == "manager" ? Manager : Moderator;
        (LiveTranscodeController controller, Mock<ILiveTranscodeService> service) = Build(caller);

        IActionResult result = await Invoke(controller, action);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(403);
        service.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("session-user")]
    public async Task Control_AllowsServerOwnerAndSessionUser(string role)
    {
        Guid caller = role == "owner" ? ServerOwner : SessionUser;
        (LiveTranscodeController controller, Mock<ILiveTranscodeService> service) = Build(caller);

        IActionResult result = await controller.ChangeQuality(
            SessionId,
            new ChangeQualityRequest("720p")
        );

        result.Should().BeOfType<OkObjectResult>();
        service.Verify(
            s =>
                s.ChangeQualityAsync(
                    SessionUser,
                    SessionId,
                    It.IsAny<ChangeQualityRequest>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    private static async Task<IActionResult> Invoke(
        LiveTranscodeController controller,
        string action
    ) =>
        action switch
        {
            "position" => controller.ReportPosition(SessionId, new ReportPositionRequest(1)),
            "buffer" => controller.ReportBufferHealth(
                SessionId,
                new ReportBufferHealthRequest(1, 1000)
            ),
            "quality" => await controller.ChangeQuality(
                SessionId,
                new ChangeQualityRequest("720p")
            ),
            "seek" => await controller.Seek(SessionId, new SeekRequest(1)),
            "end" => await controller.EndSession(SessionId),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
}
