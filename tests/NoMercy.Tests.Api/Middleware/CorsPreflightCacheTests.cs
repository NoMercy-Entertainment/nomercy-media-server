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

using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NoMercy.NmSystem.Information;
using NoMercy.Service.Configuration;
using Xunit;

namespace NoMercy.Tests.Api.Middleware;

[Trait("Category", "Middleware")]
[Collection("ConfigIsDev")]
public sealed class CorsPreflightCacheTests
{
    private static async Task<CorsPolicy> BuildPolicy(bool isDev)
    {
        bool previous = Config.IsDev;
        Config.IsDev = isDev;
        try
        {
            ServiceCollection services = new();
            services.AddLogging();
            ServiceConfiguration.ConfigureCors(services);
            using ServiceProvider provider = services.BuildServiceProvider();
            ICorsPolicyProvider policies = provider.GetRequiredService<ICorsPolicyProvider>();
            CorsPolicy? policy = await policies.GetPolicyAsync(
                new DefaultHttpContext(),
                "AllowNoMercyOrigins"
            );
            return policy!;
        }
        finally
        {
            Config.IsDev = previous;
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Preflight_response_is_cacheable_for_a_long_time(bool isDev)
    {
        CorsPolicy policy = await BuildPolicy(isDev);

        policy.PreflightMaxAge.Should().Be(TimeSpan.FromHours(24));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Existing_allowances_stay_unchanged(bool isDev)
    {
        CorsPolicy policy = await BuildPolicy(isDev);

        policy.AllowAnyMethod.Should().BeTrue();
        policy.AllowAnyHeader.Should().BeTrue();
        policy.SupportsCredentials.Should().BeTrue();
    }

    [Theory]
    [InlineData("https://nomercy.tv")]
    [InlineData("https://app.nomercy.tv")]
    [InlineData("https://cast.nomercy.tv")]
    [InlineData("https://hlsjs.video-dev.org")]
    [InlineData("http://localhost:7625")]
    public async Task Production_allows_each_first_party_origin(string origin)
    {
        CorsPolicy policy = await BuildPolicy(isDev: false);

        policy.IsOriginAllowed(origin).Should().BeTrue();
    }

    // Every self-hosted server has a name under nomercy.tv, and its owner decides
    // what it serves. Another user's server is not a first-party origin.
    [Theory]
    [InlineData("https://192-0-2-1.00000000-0000-4000-8000-000000000001.srv.nomercy.tv")]
    [InlineData("https://192-0-2-1.00000000-0000-4000-8000-000000000001.srv.nomercy.tv:7626")]
    [InlineData("https://2001-db8--1.00000000-0000-4000-8000-000000000001.srv.nomercy.tv:7626")]
    [InlineData("https://192-0-2-1.00000000-0000-4000-8000-000000000001.nomercy.tv:7626")]
    [InlineData("https://00000000-0000-4000-8000-000000000001.nomercy.tv")]
    [InlineData("https://app.nomercy.tv.example.com")]
    [InlineData("http://app.nomercy.tv")]
    public async Task Production_refuses_another_server_under_the_shared_domain(string origin)
    {
        CorsPolicy policy = await BuildPolicy(isDev: false);

        policy.IsOriginAllowed(origin).Should().BeFalse();
    }

    [Fact]
    public async Task Production_origin_list_holds_no_wildcard()
    {
        CorsPolicy policy = await BuildPolicy(isDev: false);

        policy.Origins.Should().NotContain(origin => origin.Contains('*'));
    }
}
