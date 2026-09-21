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

using Microsoft.Extensions.DependencyInjection;
using NoMercy.Plugins;
using NoMercy.Plugins.Abstractions;
using NoMercy.Plugins.Hub;
using NoMercy.Tests.Api.Infrastructure;
using Xunit;

namespace NoMercy.Tests.Api.Plugins;

/// <summary>
/// The plugin platform is wired from factory lambdas that resolve each other at
/// runtime. A ring between them does not throw: the container re-enters the
/// same factory until its stack guard parks the thread, and the server sits
/// silent forever before it ever listens. Resolving the roots of that graph on
/// the real web host, under a deadline, is the only thing that catches it.
/// </summary>
[Trait("Category", "Integration")]
public class PluginGraphResolvesTests(NoMercyApiFactory factory) : IClassFixture<NoMercyApiFactory>
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    [Theory]
    [InlineData(typeof(IPluginManager))]
    [InlineData(typeof(IPluginContextFactory))]
    [InlineData(typeof(IPluginHubContextFactory))]
    [InlineData(typeof(IPluginHubRouter))]
    public async Task PluginRoot_ResolvesWithoutReenteringItsOwnFactory(Type root)
    {
        factory.CreateClient();
        IServiceProvider services = factory.Services;

        Task<object> resolving = Task.Run(() => services.GetRequiredService(root));
        Task finished = await Task.WhenAny(resolving, Task.Delay(Deadline));

        finished
            .Should()
            .BeSameAs(
                resolving,
                because: $"{root.Name} must resolve promptly; a hang here is a dependency ring that stops the server from ever listening"
            );
        (await resolving).Should().NotBeNull();
    }
}
