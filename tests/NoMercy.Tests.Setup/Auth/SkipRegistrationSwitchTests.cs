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

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NoMercy.Database;
using NoMercy.Networking.Certificate;
using NoMercy.NmSystem.Auth;
using NoMercy.NmSystem.Configuration;
using NoMercy.NmSystem.Dto;
using NoMercy.NmSystem.Status;
using NoMercy.Setup.Auth;
using NoMercy.Tests.Setup.Infrastructure;

namespace NoMercy.Tests.Setup.Auth;

/// <summary>
/// Requirement (issue #654): a second server copy on the same machine shares the
/// hardware device id, so its registration replaces the first copy's address and
/// tunnel on nomercy.tv. NOMERCY_SKIP_REGISTER=true keeps a copy from registering,
/// assigning, or asking for a tunnel; the control plane never hears from it.
/// </summary>
[Collection(ProcessWideSetupStateCollection.Name)]
[Trait("Category", "Unit")]
public sealed class SkipRegistrationSwitchTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly string? _previous =
        Environment.GetEnvironmentVariable(ServerRegistrationSwitch.VariableName);

    public SkipRegistrationSwitchTests()
    {
        _connection = new("DataSource=:memory:;Foreign Keys=False");
        _connection.Open();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(ServerRegistrationSwitch.VariableName, _previous);
        _connection.Dispose();
    }

    private ServerRegistrationService Build(out ConnectivityStatus status)
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
        using (AppDbContext init = new(options))
            init.Database.EnsureCreated();

        Mock<IDbContextFactory<AppDbContext>> factory = new();
        factory.Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new(options));

        Mock<ICertificateService> cert = new();
        cert.Setup(c => c.RenewSslCertificate(It.IsAny<string?>(), It.IsAny<int>()))
            .Returns(Task.CompletedTask);

        status = new();
        return new(
            new AuthTokenStore(),
            factory.Object,
            Mock.Of<IUserProvisioningService>(),
            status,
            cert.Object
        );
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("1", true)]
    [InlineData("false", false)]
    [InlineData("0", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsSet_ReadsTheEnvironmentVariable(string? value, bool expected)
    {
        Environment.SetEnvironmentVariable(ServerRegistrationSwitch.VariableName, value);

        Assert.Equal(expected, ServerRegistrationSwitch.IsSet);
    }

    [Fact]
    public async Task Init_WithSwitchSet_NeverCallsTheControlPlane()
    {
        using LoopbackHttpServer server = new();
        using ExternalServicesConfigScope scope = new(apiServerBaseUrl: server.BaseUrl);
        Environment.SetEnvironmentVariable(ServerRegistrationSwitch.VariableName, "true");
        ServerRegistrationService service = Build(out _);

        await service.Init(maxRetries: 1);

        Assert.Equal(0, server.RequestCount);
    }

    [Fact]
    public async Task Init_WithoutSwitch_StillRegisters()
    {
        using LoopbackHttpServer server = new();
        server.Handler = _ => new(200, "{}");
        using ExternalServicesConfigScope scope = new(apiServerBaseUrl: server.BaseUrl);
        Environment.SetEnvironmentVariable(ServerRegistrationSwitch.VariableName, null);
        ServerRegistrationService service = Build(out _);

        // The assign step fails on the empty body; only the register call matters here.
        await Record.ExceptionAsync(() => service.Init(maxRetries: 1));

        Assert.True(server.RequestCount >= 1);
    }

    [Fact]
    public async Task GetTunnelAvailability_WithSwitchSet_NeverCallsTheControlPlane()
    {
        using LoopbackHttpServer server = new();
        using ExternalServicesConfigScope scope = new(apiServerBaseUrl: server.BaseUrl);
        Environment.SetEnvironmentVariable(ServerRegistrationSwitch.VariableName, "true");
        ServerRegistrationService service = Build(out ConnectivityStatus status);

        await service.GetTunnelAvailability();

        Assert.Equal(0, server.RequestCount);
        Assert.Equal(TunnelAvailability.NotProvisioned, status.TunnelAvailability);
    }
}
