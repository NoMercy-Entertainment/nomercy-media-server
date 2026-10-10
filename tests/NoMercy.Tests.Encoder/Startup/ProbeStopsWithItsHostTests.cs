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
using NoMercy.Database;
using NoMercy.Encoder.Composition;
using NoMercy.Encoder.Startup;

namespace NoMercy.Tests.Encoder.Startup;

/// <summary>
/// The hardware probe defers itself until <see cref="NoMercy.NmSystem.Lifecycle.BootStage.Binaries"/>,
/// which is process-wide on purpose so it survives the HTTP→HTTPS restart of first boot.
/// That makes stopping it on host shutdown essential: the token
/// <see cref="Microsoft.Extensions.Hosting.IHostedService.StartAsync"/> receives signals a
/// failed startup, not a stop, so the setup host's copy of this service sat waiting right
/// through its own disposal and then ran against a dead container.
/// <para>
/// Observed on a first boot (2026-08-02): "Could not load driver fingerprint from
/// AppDbContext.Configuration" and "Could not save driver fingerprint", both
/// <c>ObjectDisposedException: IServiceProvider</c>. The fingerprint is what lets a boot
/// skip re-benchmarking, so every start re-ran a multi-minute CPU-saturating benchmark.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public class ProbeStopsWithItsHostTests : IDisposable
{
    private readonly List<ServiceProvider> _providers = [];

    // HostBuilder normally supplies this; the encoder's background services need it to
    // construct, and this test never runs a real host.
    private sealed class TestHostLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted { get; } = new(true);
        public CancellationToken ApplicationStopping { get; } = CancellationToken.None;
        public CancellationToken ApplicationStopped { get; } = CancellationToken.None;

        public void StopApplication() { }
    }

    public void Dispose()
    {
        foreach (ServiceProvider provider in _providers)
            provider.Dispose();

        GC.SuppressFinalize(this);
    }

    private ServiceProvider BuildProvider()
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton<IHostApplicationLifetime, TestHostLifetime>();
        services.AddDbContextFactory<MediaContext>(o => o.UseInMemoryDatabase("probe-media"));
        services.AddDbContextFactory<AppDbContext>(o => o.UseInMemoryDatabase("probe-app"));
        services.AddNoMercyEncoder(opts =>
        {
            opts.FfmpegPathOverride = "ffmpeg";
            opts.FfprobePathOverride = "ffprobe";
        });

        ServiceProvider provider = services.BuildServiceProvider();
        _providers.Add(provider);
        return provider;
    }

    [Fact]
    public async Task StoppingTheService_EndsTheDeferredProbe()
    {
        ServiceProvider provider = BuildProvider();
        HardwareInitializationService service =
            provider.GetRequiredService<HardwareInitializationService>();

        // No stage is ever marked, so the probe is parked on BootStage.Binaries exactly as
        // the setup host's copy is while the HTTPS host takes over.
        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        // Completing at all is the assertion: before StopAsync cancelled it, this task
        // stayed alive past its host and woke up holding a disposed provider.
        Task finished = await Task.WhenAny(service.DetectionTask, Task.Delay(5000));

        finished.Should().BeSameAs(service.DetectionTask, "the probe must end with its host");
    }
}
