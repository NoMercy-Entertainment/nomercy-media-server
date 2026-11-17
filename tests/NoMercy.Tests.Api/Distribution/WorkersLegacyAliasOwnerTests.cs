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
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NoMercy.Encoder.Composition;
using NoMercy.Encoder.Distribution;
using NoMercy.NmSystem.Information;
using NoMercy.Service;
using NoMercy.Tests.Api.Infrastructure;
using Xunit;

namespace NoMercy.Tests.Api.Distribution;

/// <summary>
/// Register, heartbeat and unregister let a signed worker in without a bearer
/// token (#228). On the legacy /dashboard/workers alias, which the signature check
/// does not cover, they must still answer to the server owner only, as before.
/// </summary>
[Trait("Category", "Integration")]
public class WorkersLegacyAliasOwnerTests(NoMercyApiFactory factory)
    : IClassFixture<NoMercyApiFactory>
{
    private const string Alias = "/api/v1/dashboard/workers";

    [Fact]
    public async Task Owner_CanStillRegisterHeartbeatAndUnregister_OnTheLegacyAlias()
    {
        using WebApplicationFactory<Startup> coordinator = StartCoordinator();
        using HttpClient owner = coordinator.CreateClient().AsAuthenticated();

        using HttpResponseMessage register = await owner.PostAsync(
            $"{Alias}/register",
            RegisterBody("owner-worker")
        );
        using HttpResponseMessage heartbeat = await owner.PostAsync(
            $"{Alias}/owner-worker/heartbeat",
            new StringContent("{}", Encoding.UTF8, "application/json")
        );
        using HttpResponseMessage unregister = await owner.DeleteAsync($"{Alias}/owner-worker");

        register.StatusCode.Should().Be(HttpStatusCode.OK);
        heartbeat.StatusCode.Should().Be(HttpStatusCode.OK);
        unregister.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task SignedInNonOwner_IsRefused_OnTheLegacyAlias()
    {
        using WebApplicationFactory<Startup> coordinator = StartCoordinator();
        using HttpClient member = coordinator.CreateClient().AsSecondaryUser();

        using HttpResponseMessage register = await member.PostAsync(
            $"{Alias}/register",
            RegisterBody("member-worker")
        );

        register.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        coordinator
            .Services.GetRequiredService<InMemoryRemoteWorkerRegistry>()
            .GetActiveWorkers()
            .Should()
            .NotContain(w => w.WorkerId == "member-worker");
    }

    // The /distribution/ route keeps demanding a signature even from the owner:
    // the fix let signed workers in, it did not let bearer tokens skip the check.
    [Fact]
    public async Task Owner_WithoutASignature_IsStillRefused_OnTheDistributionRoute()
    {
        using WebApplicationFactory<Startup> coordinator = StartCoordinator();
        using HttpClient owner = coordinator.CreateClient().AsAuthenticated();

        using HttpResponseMessage register = await owner.PostAsync(
            "/api/v1/distribution/workers/register",
            RegisterBody("unsigned-owner-worker")
        );

        register.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private WebApplicationFactory<Startup> StartCoordinator() =>
        factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<EncoderOptions>();
                services.AddSingleton(
                    new EncoderOptions
                    {
                        DistributedEncodingSigningKey = "legacy-alias-signing-key",
                        WorkerRegistryPath = Path.Combine(
                            AppFiles.DataPath,
                            "distribution",
                            "workers.json"
                        ),
                    }
                );
            })
        );

    private static StringContent RegisterBody(string workerId) =>
        new(
            $$"""{"worker_id":"{{workerId}}","base_url":"https://worker.example","cpu_cores":4}""",
            Encoding.UTF8,
            "application/json"
        );
}
