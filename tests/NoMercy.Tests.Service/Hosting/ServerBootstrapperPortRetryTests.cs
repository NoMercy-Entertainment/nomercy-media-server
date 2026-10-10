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

using System.IdentityModel.Tokens.Jwt;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NoMercy.Database;
using NoMercy.Networking.Certificate;
using NoMercy.NmSystem.Auth;
using NoMercy.NmSystem.Configuration;
using NoMercy.Service.Hosting;
using NoMercy.Setup.Auth;
using NoMercy.Setup.Server;
using NoMercy.Storage;
using NoMercy.Tests.Common;

namespace NoMercy.Tests.Service.Hosting;

[Trait("Category", "Unit")]
public sealed class ServerBootstrapperPortRetryTests
{
    [Fact]
    public void PortRetry_RestoresFreshContainerBeforeRunningHost()
    {
        string source = File.ReadAllText(
            RepoPaths.At("src/NoMercy.Service/Hosting/ServerBootstrapper.cs")
        );
        int retryStart = source.IndexOf("if (shouldRetry)", StringComparison.Ordinal);
        Assert.True(retryStart >= 0, "port retry branch moved");
        string retryBranch = source[retryStart..];

        int restore = retryBranch.IndexOf(
            "await RestorePortRetryStateAsync(retryHost.Services)",
            StringComparison.Ordinal
        );
        int run = retryBranch.IndexOf(
            "await retryServerRunner.RunHost(retryHost)",
            StringComparison.Ordinal
        );

        Assert.True(restore >= 0, "retry host must restore its per-container state");
        Assert.True(run > restore, "retry host must restore state before serving requests");
    }

    [Fact]
    public async Task PortRetry_WithValidToken_RestoresTokenAndCompletesSetup()
    {
        await using AppDbContext context = CreateContext();
        string token = new JwtSecurityTokenHandler().WriteToken(
            new JwtSecurityToken(
                issuer: ExternalServicesConfig.Current.AuthBaseUrl,
                audience: "nomercy-server",
                notBefore: DateTime.UtcNow.AddMinutes(-5),
                expires: DateTime.UtcNow.AddHours(2)
            )
        );
        context.Configuration.Add(
            new()
            {
                Key = "auth_access_token",
                Value = string.Empty,
                SecureValue = token,
            }
        );
        await context.SaveChangesAsync();

        AuthTokenStore tokenStore = new();
        SetupState setupState = new();
        using CancellationTokenSource cancellation = new();
        using ServiceProvider services = CreateServices(
            context,
            tokenStore,
            setupState,
            cancellation
        );

        await ServerBootstrapper.RestorePortRetryStateAsync(services);

        Assert.Equal(token, tokenStore.AccessToken);
        Assert.Equal(SetupPhase.Complete, setupState.CurrentPhase);
        Assert.False(setupState.IsSetupRequired);
    }

    private static AppDbContext CreateContext()
    {
        DbContextOptionsBuilder<AppDbContext> options = new();
        options.UseSqlite("Data Source=:memory:");
        AppDbContext context = new(options.Options);
        context.Database.OpenConnection();
        context.Database.EnsureCreated();
        return context;
    }

    private static ServiceProvider CreateServices(
        AppDbContext context,
        AuthTokenStore tokenStore,
        SetupState setupState,
        CancellationTokenSource cancellation
    )
    {
        Mock<IShutdownCoordinator> shutdownCoordinator = new();
        shutdownCoordinator.SetupGet(x => x.Token).Returns(cancellation.Token);
        Mock<ICertificateService> certificateService = new();
        certificateService.Setup(x => x.HasValidCertificate()).Returns(true);

        ServiceCollection registrations = new();
        registrations.AddSingleton<ICertificateService>(certificateService.Object);
        registrations.AddSingleton<IShutdownCoordinator>(shutdownCoordinator.Object);
        registrations.AddSingleton(setupState);
        registrations.AddSingleton<AuthManager>(_ =>
            new(context, Mock.Of<IStorageDriver>(), tokenStore)
        );
        return registrations.BuildServiceProvider();
    }
}
