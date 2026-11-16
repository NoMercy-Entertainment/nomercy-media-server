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

using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using Newtonsoft.Json.Linq;
using NoMercy.Api.Services;
using NoMercy.Data.ServerBackups;
using NoMercy.Service;
using NoMercy.Tests.Api.Infrastructure;
using Xunit;

namespace NoMercy.Tests.Api.Dashboard;

/// <summary>
/// The backup endpoints through the real pipeline, with the backup services and
/// the restarter replaced, so no test copies a database or restarts anything.
/// </summary>
[Trait("Category", "Integration")]
public sealed class BackupsControllerTests(NoMercyApiFactory factory)
    : IClassFixture<NoMercyApiFactory>
{
    private const string Route = "/api/v1/dashboard/backups";
    private const string BackupId = "20261116-040000-0000000";

    private readonly Mock<IBackupService> _backups = new();
    private readonly Mock<IRestoreScheduler> _restores = new();
    private readonly Mock<IServerRestarter> _restarter = new();

    private WebApplicationFactory<Startup> Host() =>
        factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IBackupService>();
                services.RemoveAll<IRestoreScheduler>();
                services.RemoveAll<IServerRestarter>();
                services.AddSingleton(_backups.Object);
                services.AddSingleton(_restores.Object);
                services.AddSingleton(_restarter.Object);
            })
        );

    private static BackupSetSummary Summary(string id = BackupId) =>
        new(
            id,
            new DateTime(2026, 11, 16, 4, 0, 0, DateTimeKind.Utc),
            "made by the owner",
            "1.2.3",
            2048,
            [
                new BackupManifestDatabase
                {
                    Name = "media",
                    Size = 2048,
                    LastMigration = "20261103222056_FixWatchProviderUniqueness",
                },
            ],
            4
        );

    private void ScheduleSucceeds() =>
        _restores
            .Setup(scheduler => scheduler.ScheduleAsync(BackupId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(
                new RestoreScheduleResult(
                    RestoreScheduleStatus.Scheduled,
                    [],
                    new PendingRestore
                    {
                        BackupId = BackupId,
                        SafetyBackupId = "20261116-050000-0000000",
                    }
                )
            );

    // ── authorization ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("GET", Route)]
    [InlineData("POST", Route)]
    [InlineData("POST", Route + "/" + BackupId + "/restore")]
    public async Task ASignedInNonOwner_IsRefused_AndNothingHappens(string method, string url)
    {
        using WebApplicationFactory<Startup> host = Host();
        using HttpClient member = host.CreateClient().AsSecondaryUser();

        using HttpResponseMessage response = await member.SendAsync(new(new(method), url));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _backups.Invocations.Should().BeEmpty();
        _restores.Invocations.Should().BeEmpty();
        _restarter.Invocations.Should().BeEmpty();
    }

    [Theory]
    [InlineData("GET", Route)]
    [InlineData("POST", Route)]
    [InlineData("POST", Route + "/" + BackupId + "/restore")]
    public async Task AnAnonymousCaller_IsRefused(string method, string url)
    {
        using WebApplicationFactory<Startup> host = Host();
        using HttpClient anonymous = host.CreateClient().AsUnauthenticated();

        using HttpResponseMessage response = await anonymous.SendAsync(new(new(method), url));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _restores.Invocations.Should().BeEmpty();
        _restarter.Invocations.Should().BeEmpty();
    }

    // ── behaviour ────────────────────────────────────────────────────────────

    [Fact]
    public async Task List_ShowsTheBackupsTheWaitingRestoreAndTheLastResult()
    {
        _backups
            .Setup(service => service.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([Summary()]);
        _restores
            .Setup(scheduler => scheduler.GetLastResultAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(
                new RestoreResult
                {
                    BackupId = BackupId,
                    Status = RestoreStatus.RolledBack,
                    Message = "undone",
                }
            );
        using WebApplicationFactory<Startup> host = Host();
        using HttpClient owner = host.CreateClient().AsAuthenticated();

        using HttpResponseMessage response = await owner.GetAsync(Route);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        JObject body = JObject.Parse(await response.Content.ReadAsStringAsync());
        body["backups"]![0]!["id"]!.Value<string>().Should().Be(BackupId);
        body["backups"]![0]!["config_file_count"]!.Value<int>().Should().Be(4);
        body["backups"]![0]!["databases"]![0]!["last_migration"]!
            .Value<string>()
            .Should()
            .Be("20261103222056_FixWatchProviderUniqueness");
        body["pending_restore"]!.Type.Should().Be(JTokenType.Null);
        body["last_restore"]!["status"]!.Value<string>().Should().Be("rolled_back");
    }

    [Fact]
    public async Task Create_MakesABackupNow()
    {
        _backups
            .Setup(service =>
                service.CreateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())
            )
            .ReturnsAsync(Summary());
        using WebApplicationFactory<Startup> host = Host();
        using HttpClient owner = host.CreateClient().AsAuthenticated();

        using HttpResponseMessage response = await owner.PostAsync(Route, null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        JObject.Parse(await response.Content.ReadAsStringAsync())["id"]!
            .Value<string>()
            .Should()
            .Be(BackupId);
        _backups.Verify(
            service => service.CreateAsync("made by the owner", It.IsAny<CancellationToken>()),
            Times.Once
        );
    }

    [Fact]
    public async Task Create_WhenTheBackupFails_AnswersWithAServerError()
    {
        _backups
            .Setup(service =>
                service.CreateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())
            )
            .ThrowsAsync(new IOException("disk full"));
        using WebApplicationFactory<Startup> host = Host();
        using HttpClient owner = host.CreateClient().AsAuthenticated();

        using HttpResponseMessage response = await owner.PostAsync(Route, null);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
    }

    [Fact]
    public async Task Restore_SchedulesThenRestarts()
    {
        ScheduleSucceeds();
        _restarter
            .Setup(restarter => restarter.Begin())
            .Returns(new ServerRestartOutcome(ServerRestartOutcome.Supervisor, null));
        using WebApplicationFactory<Startup> host = Host();
        using HttpClient owner = host.CreateClient().AsAuthenticated();

        using HttpResponseMessage response = await owner.PostAsync(
            $"{Route}/{BackupId}/restore",
            null
        );

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        JObject body = JObject.Parse(await response.Content.ReadAsStringAsync());
        body["backup_id"]!.Value<string>().Should().Be(BackupId);
        body["safety_backup_id"]!.Value<string>().Should().Be("20261116-050000-0000000");
        body["mode"]!.Value<string>().Should().Be(ServerRestartOutcome.Supervisor);
        _restarter.Verify(restarter => restarter.Begin(), Times.Once);
        _restores.Verify(
            scheduler => scheduler.CancelAsync(It.IsAny<CancellationToken>()),
            Times.Never
        );
    }

    [Fact]
    public async Task Restore_WhenTheServerCannotRestart_WithdrawsTheRequest()
    {
        ScheduleSucceeds();
        _restarter
            .Setup(restarter => restarter.Begin())
            .Returns(new ServerRestartOutcome(ServerRestartOutcome.Relaunch, "no executable"));
        using WebApplicationFactory<Startup> host = Host();
        using HttpClient owner = host.CreateClient().AsAuthenticated();

        using HttpResponseMessage response = await owner.PostAsync(
            $"{Route}/{BackupId}/restore",
            null
        );

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        _restores.Verify(
            scheduler => scheduler.CancelAsync(It.IsAny<CancellationToken>()),
            Times.Once
        );
        _restarter.Verify(
            restarter => restarter.Complete(It.IsAny<ServerRestartOutcome>()),
            Times.Never
        );
    }

    [Theory]
    [InlineData(RestoreScheduleStatus.InvalidId, HttpStatusCode.BadRequest)]
    [InlineData(RestoreScheduleStatus.NotFound, HttpStatusCode.NotFound)]
    [InlineData(RestoreScheduleStatus.AlreadyPending, HttpStatusCode.Conflict)]
    [InlineData(RestoreScheduleStatus.Invalid, HttpStatusCode.UnprocessableEntity)]
    public async Task Restore_ThatIsNotScheduled_AnswersWhyAndDoesNotRestart(
        RestoreScheduleStatus status,
        HttpStatusCode expected
    )
    {
        _restores
            .Setup(scheduler => scheduler.ScheduleAsync(BackupId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RestoreScheduleResult(status, ["checksum mismatch"], null));
        using WebApplicationFactory<Startup> host = Host();
        using HttpClient owner = host.CreateClient().AsAuthenticated();

        using HttpResponseMessage response = await owner.PostAsync(
            $"{Route}/{BackupId}/restore",
            null
        );

        response.StatusCode.Should().Be(expected);
        _restarter.Verify(restarter => restarter.Begin(), Times.Never);
    }
}
