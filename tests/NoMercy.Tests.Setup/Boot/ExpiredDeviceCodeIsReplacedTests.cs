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
using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.Networking.Certificate;
using NoMercy.NmSystem.Auth;
using NoMercy.NmSystem.Configuration;
using NoMercy.Setup.Auth;
using NoMercy.Setup.Boot;
using NoMercy.Setup.Server;
using NoMercy.Storage.Drivers.Local;
using NoMercy.Tests.Setup.Infrastructure;

namespace NoMercy.Tests.Setup.Boot;

/// <summary>
/// A device code lives about ten minutes. A first-time user spends that budget
/// registering an account, accepting the terms and verifying an email address, so the
/// code routinely dies before anyone can approve it. Observed on a real production
/// onboarding on 2026-08-02: the console printed
/// "Device code flow ended: expired_token" and the server then sat in setup mode for
/// half an hour with no code on screen and no way to obtain one short of a restart.
/// <para>
/// The browser setup page has always minted a replacement shortly before expiry. The
/// console path — the only one a Docker or headless user has — did not, because
/// <c>PollDeviceGrant</c> returned <c>void</c>: an expired code and a granted one were
/// indistinguishable to the caller, so there was nothing to retry on. These tests pin
/// the outcome the caller now branches on.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
[Collection(ProcessWideSetupStateCollection.Name)]
public class ExpiredDeviceCodeIsReplacedTests : IDisposable
{
    private readonly string _originalAuthBaseUrl = ExternalServicesConfig.Current.AuthBaseUrl;

    public void Dispose()
    {
        ExternalServicesConfig.Current.AuthBaseUrl = _originalAuthBaseUrl;
        GC.SuppressFinalize(this);
    }

    private static BootOrchestrator BuildOrchestrator()
    {
        // No database is created on purpose: none of these paths reach a grant, and the
        // token store is the only thing that would touch it.
        return new(
            new SetupState(),
            new AuthManager(new(), new LocalStorageDriver(), new AuthTokenStore()),
            Mock.Of<IApiKeyLoader>(),
            Mock.Of<IDegradedModeRecovery>(),
            Mock.Of<IServerRegistrationService>(),
            new AuthTokenStore(),
            new CertificateService(NullLogger<CertificateService>.Instance, null!),
            new RealHttpClientFactory()
        );
    }

    private sealed class RealHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    /// <summary>
    /// Drives the real HTTP path over loopback. The attempt is private because the public
    /// entry point refuses to run on a desktop OS, and the test host is Windows.
    /// </summary>
    private static async Task<string> RunOneAttempt(BootOrchestrator orchestrator)
    {
        MethodInfo attempt =
            typeof(BootOrchestrator).GetMethod(
                "RunOneDeviceCodeAttemptAsync",
                BindingFlags.NonPublic | BindingFlags.Instance
            ) ?? throw new MissingMethodException("RunOneDeviceCodeAttemptAsync");

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        object task = attempt.Invoke(orchestrator, [cts.Token])!;
        await (Task)task;

        object result = task.GetType().GetProperty("Result")!.GetValue(task)!;
        return result.ToString()!;
    }

    [Fact]
    public async Task AnExpiredCode_IsReportedAsEnded_SoTheCallerCanMintAnother()
    {
        using LoopbackHttpServer auth = new();
        auth.Handler = request =>
            request.Path.Contains("/auth/device")
                ? new(
                    200,
                    """
                    {"device_code":"dc-1","user_code":"AAAA-BBBB","verification_uri":"http://127.0.0.1/device","verification_uri_complete":"http://127.0.0.1/device?user_code=AAAA-BBBB","interval":1,"expires_in":600}
                    """
                )
                : new(400, """{"error":"expired_token"}""");

        ExternalServicesConfig.Current.AuthBaseUrl = auth.BaseUrl;

        string outcome = await RunOneAttempt(BuildOrchestrator());

        outcome
            .Should()
            .Be(
                "Ended",
                "an expired code has to be distinguishable from a grant or the flow cannot retry"
            );
    }

    /// <summary>
    /// A declined code is the same story: the server is useless without a login, so the
    /// user gets another chance rather than a dead process.
    /// </summary>
    [Fact]
    public async Task ADeclinedCode_IsReportedAsEnded()
    {
        using LoopbackHttpServer auth = new();
        auth.Handler = request =>
            request.Path.Contains("/auth/device")
                ? new(
                    200,
                    """
                    {"device_code":"dc-2","user_code":"CCCC-DDDD","verification_uri":"http://127.0.0.1/device","verification_uri_complete":"http://127.0.0.1/device?user_code=CCCC-DDDD","interval":1,"expires_in":600}
                    """
                )
                : new(400, """{"error":"access_denied"}""");

        ExternalServicesConfig.Current.AuthBaseUrl = auth.BaseUrl;

        string outcome = await RunOneAttempt(BuildOrchestrator());

        outcome.Should().Be("Ended");
    }

    /// <summary>
    /// A code that was never issued must NOT read as an expiry — nothing was shown to the
    /// user, so the caller backs off and retries instead of announcing a new code.
    /// </summary>
    [Fact]
    public async Task AnAuthServerThatRefusesToIssue_IsReportedAsUnreachable()
    {
        using LoopbackHttpServer auth = new();
        auth.Handler = _ => new(503, "service unavailable");

        ExternalServicesConfig.Current.AuthBaseUrl = auth.BaseUrl;

        string outcome = await RunOneAttempt(BuildOrchestrator());

        outcome.Should().Be("Unreachable");
    }
}
