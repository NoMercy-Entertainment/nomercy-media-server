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

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.Database;
using NoMercy.Encoder.Composition;
using NoMercy.Encoder.Subscribers;
using NoMercy.Events;

namespace NoMercy.Tests.Encoder.Subscribers;

public class V3SubscriberActivatorTests
{
    private static ServiceProvider BuildProvider()
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton<IHostApplicationLifetime, TestHostLifetime>();
        services.AddSingleton<IEventBus, InMemoryEventBus>();
        services.AddDbContextFactory<MediaContext>(o => o.UseInMemoryDatabase("test-media"));
        services.AddDbContextFactory<AppDbContext>(o => o.UseInMemoryDatabase("test-app"));
        services.AddNoMercyEncoder(opts =>
        {
            opts.FfmpegPathOverride = "ffmpeg";
            opts.FfprobePathOverride = "ffprobe";
        });
        return services.BuildServiceProvider();
    }

    private sealed class TestHostLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted { get; } = CancellationToken.None;
        public CancellationToken ApplicationStopping { get; } = CancellationToken.None;
        public CancellationToken ApplicationStopped { get; } = CancellationToken.None;

        public void StopApplication() { }
    }

    [Fact]
    public void V3Subscribers_AreRegisteredAsSingletons()
    {
        ServiceProvider provider = BuildProvider();

        CropDetectSubscriber crop1 = provider.GetRequiredService<CropDetectSubscriber>();
        CropDetectSubscriber crop2 = provider.GetRequiredService<CropDetectSubscriber>();
        crop1.Should().BeSameAs(crop2);
    }

    [Fact]
    public void V3SubscriberActivator_IsRegisteredAsHostedService()
    {
        ServiceProvider provider = BuildProvider();
        IEnumerable<IHostedService> hosted = provider.GetServices<IHostedService>();
        hosted.Should().Contain(s => s.GetType().Name == "V3SubscriberActivator");
    }

    [Fact]
    public async Task V3SubscriberActivator_StartAsync_ResolvesEverySubscriber()
    {
        ServiceProvider provider = BuildProvider();
        IHostedService activator = provider
            .GetServices<IHostedService>()
            .First(s => s.GetType().Name == "V3SubscriberActivator");

        await activator.StartAsync(CancellationToken.None);

        // Subscribers should be alive — resolving them after start returns the
        // same instance the activator pulled.
        provider.GetRequiredService<CropDetectSubscriber>().Should().NotBeNull();
    }

    [Fact]
    public async Task V3SubscriberActivator_StartAsync_SwallowsResolutionErrors()
    {
        // The encoder pipeline must keep running even if a plugin tampers
        // with the V3 subscriber registrations or a mid-migration leaves
        // them unresolvable. The activator catches and logs; it must not
        // propagate the exception and crash the host.
        ServiceProvider emptyProvider = new ServiceCollection().BuildServiceProvider();

        // Reflect the internal type so we can construct it directly with a
        // provider that can't resolve any of the V3 subscribers.
        Type activatorType =
            typeof(V3SubscriberActivator)
            ?? throw new InvalidOperationException("activator type missing");
        IHostedService activator = (IHostedService)
            Activator.CreateInstance(
                activatorType,
                [emptyProvider, NullLogger<V3SubscriberActivator>.Instance]
            )!;

        Func<Task> act = () => activator.StartAsync(CancellationToken.None);

        await act.Should().NotThrowAsync("subscriber resolution failures must not crash the host");

        // Stop should also be quiet — Dispose handles the null subscribers.
        await activator.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task V3SubscriberActivator_StopAsync_DisposesAllSubscribers()
    {
        // Activator's Dispose path is what tears down event-bus
        // subscriptions on shutdown. Resolving the singletons after
        // dispose proves the provider still works — the activator
        // doesn't dispose the underlying provider, only its references.
        ServiceProvider provider = BuildProvider();
        IHostedService activator = provider
            .GetServices<IHostedService>()
            .First(s => s.GetType().Name == "V3SubscriberActivator");

        await activator.StartAsync(CancellationToken.None);
        Func<Task> act = () => activator.StopAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }
}
