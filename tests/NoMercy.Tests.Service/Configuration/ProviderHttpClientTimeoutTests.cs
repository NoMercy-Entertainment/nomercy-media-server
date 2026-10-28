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
using Microsoft.Extensions.DependencyInjection;
using NoMercy.NmSystem.Information;
using NoMercy.Providers.Helpers;
using NoMercy.Service.Configuration;

namespace NoMercy.Tests.Service.Configuration;

[Trait("Category", "Unit")]
public sealed class ProviderHttpClientTimeoutTests
{
    private static HttpClient CreateClient(string name)
    {
        Mock<IServerConfiguration> config = new();
        config.SetupGet(c => c.UserAgent).Returns("NoMercy test agent");

        ServiceCollection services = new();
        services.AddSingleton(config.Object);
        MethodInfo configure = typeof(ServiceConfiguration).GetMethod(
            "ConfigureHttpClients",
            BindingFlags.NonPublic | BindingFlags.Static
        )!;
        configure.Invoke(null, [services]);

        using ServiceProvider provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IHttpClientFactory>().CreateClient(name);
    }

    [Fact]
    public void JikanClient_GivesUpWithinHalfAMinute_SoAnOfflineProviderCannotStallStartup()
    {
        HttpClient client = CreateClient(HttpClientNames.Jikan);

        client.Timeout.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(30));
    }
}
