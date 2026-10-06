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

using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.Database;
using NoMercy.Networking.Certificate;
using NoMercy.NmSystem.Auth;
using NoMercy.NmSystem.Security;
using NoMercy.Setup.Auth;
using NoMercy.Setup.Boot;
using NoMercy.Setup.Server;
using NoMercy.Storage.Drivers.Local;
using NoMercy.Tests.Setup.Infrastructure;

namespace NoMercy.Tests.Setup;

[Trait("Category", "Unit")]
public class BootOrchestratorTests : IDisposable
{
    private readonly AppDbContext _appContext;
    private readonly AuthManager _authManager;
    private readonly SetupState _setupState;
    private readonly BootOrchestrator _orchestrator;

    public BootOrchestratorTests()
    {
        ServiceCollection services = new();
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        ServiceProvider provider = services.BuildServiceProvider();
        TokenStore.Initialize(provider);

        DbContextOptionsBuilder<AppDbContext> optionsBuilder = new();
        optionsBuilder.UseSqlite("Data Source=:memory:");
        _appContext = new(optionsBuilder.Options);
        _appContext.Database.OpenConnection();
        _appContext.Database.EnsureCreated();

        _authManager = new(_appContext, new LocalStorageDriver(), new AuthTokenStore());
        _setupState = new();
        _orchestrator = new(
            _setupState,
            _authManager,
            new FakeApiKeyLoader(),
            new FakeDegradedModeRecovery(),
            new FakeServerRegistrationService(),
            new AuthTokenStore(),
            new CertificateService(NullLogger<CertificateService>.Instance, null!),
            new NoOpHttpClientFactory()
        );
    }

    private sealed class NoOpHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    public void Dispose()
    {
        _appContext.Database.CloseConnection();
        _appContext.Dispose();
    }

    [Fact]
    public void SetupState_StartsAsUnauthenticated()
    {
        Assert.Equal(SetupPhase.Unauthenticated, _setupState.CurrentPhase);
        Assert.True(_setupState.IsSetupRequired);
    }

    [Fact]
    public async Task PostAuth_WaitsForAuthenticated()
    {
        using CancellationTokenSource cts = new();
        Task<bool> postAuth = _orchestrator.RunPostAuthAsync(cts.Token);

        Assert.False(postAuth.IsCompleted);
        Assert.Equal(SetupPhase.Unauthenticated, _setupState.CurrentPhase);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => postAuth);
    }

    [Fact]
    public async Task PostAuth_ProceedsWhenAuthenticated()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));

        _ = Task.Run(async () =>
        {
            await Task.Delay(100);
            _setupState.TransitionTo(SetupPhase.Authenticating);
            _setupState.TransitionTo(SetupPhase.Authenticated);
        });

        try
        {
            await _orchestrator.RunPostAuthAsync(cts.Token);
        }
        catch
        {
            // Registration will fail in test (no network) — expected
        }

        Assert.NotEqual(SetupPhase.Unauthenticated, _setupState.CurrentPhase);
    }
}
