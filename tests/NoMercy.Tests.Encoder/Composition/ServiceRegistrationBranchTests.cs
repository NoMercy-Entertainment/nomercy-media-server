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
using NoMercy.Encoder.Bundle;
using NoMercy.Encoder.Codecs;
using NoMercy.Encoder.Composition;
using NoMercy.Encoder.ContentAnalysis;
using NoMercy.Encoder.Devices;
using NoMercy.Encoder.Distribution;
using NoMercy.Encoder.Notifications;
using NoMercy.Encoder.Output;
using NoMercy.Encoder.Strategies;
using NoMercy.Encoder.Subtitles;

namespace NoMercy.Tests.Encoder.Composition;

/// <summary>
/// Branch coverage for AddNoMercyEncoder beyond the core-service smoke
/// tests: verifies multi-impl resolution counts, mode-specific factories
/// (coordinator vs standalone IRemoteWorkerRegistry), and presence of
/// services with no other test coverage. A missing IServiceCollection
/// line surfaces here instead of in production at first ffmpeg call.
/// </summary>
public class ServiceRegistrationBranchTests
{
    private static ServiceProvider BuildProvider(Action<EncoderOptions>? extra = null)
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton<IHostApplicationLifetime, TestHostLifetime>();
        services.AddDbContextFactory<MediaContext>(o => o.UseInMemoryDatabase("test-media"));
        services.AddDbContextFactory<AppDbContext>(o => o.UseInMemoryDatabase("test-app"));
        services.AddNoMercyEncoder(opts =>
        {
            opts.FfmpegPathOverride = "ffmpeg";
            opts.FfprobePathOverride = "ffprobe";
            extra?.Invoke(opts);
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
    public void OutputStrategies_AreAllRegistered()
    {
        // Eight strategies in the source: HLS, MKV, MP4, DASH, MP3, FLAC,
        // OGG, AudioHLS. A future merge that drops one would silently
        // remove a container option from the catalogue — pin the count.
        ServiceProvider provider = BuildProvider();
        IEnumerable<IOutputStrategy> strategies = provider.GetServices<IOutputStrategy>();
        strategies.Should().HaveCount(8);
    }

    [Fact]
    public void EncodingStrategies_AreAllRegistered()
    {
        // Eleven strategies covering single-pass + two-pass HLS/MP4/DASH
        // plus single-pass MKV/MP3/FLAC/OGG/AudioHLS.
        ServiceProvider provider = BuildProvider();
        IEnumerable<IEncodingStrategy> strategies = provider.GetServices<IEncodingStrategy>();
        strategies.Should().HaveCount(11);
    }

    [Fact]
    public void QualityScalers_AreAllRegistered_WithLinearAsFallback()
    {
        // Six scalers — NVENC, QSV, VideoToolbox, AmfAv1, SvtAv1, Linear.
        // The linear scaler is the universal fallback and must be registered
        // LAST so the resolver picks vendor-specific scalers first.
        ServiceProvider provider = BuildProvider();
        List<IQualityScaler> scalers = provider.GetServices<IQualityScaler>().ToList();
        scalers.Should().HaveCount(6);
        scalers[^1]
            .Should()
            .BeOfType<LinearQualityScaler>(
                "linear scaler must be registered last so vendor-specific impls win in the resolver"
            );
    }

    [Fact]
    public void RemoteWorkerRegistry_StandaloneMode_UsesInMemory()
    {
        // No signing key + no coordinator URL → standalone install. The
        // registry must be the in-memory variant; the JSON-persisted variant
        // wraps the in-memory one only when IsCoordinatorMode is true.
        ServiceProvider provider = BuildProvider();
        IRemoteWorkerRegistry registry = provider.GetRequiredService<IRemoteWorkerRegistry>();

        registry.Should().BeOfType<InMemoryRemoteWorkerRegistry>();
    }

    [Fact]
    public void RemoteWorkerRegistry_CoordinatorMode_WrapsWithJsonPersistence()
    {
        // Signing key set + CoordinatorUrl unset → coordinator mode. The
        // registry wraps the in-memory one with disk persistence so worker
        // identities survive a coordinator restart.
        string tempDir = Path.Combine(Path.GetTempPath(), $"nm-coord-test-{Guid.NewGuid():N}");
        try
        {
            ServiceProvider provider = BuildProvider(opts =>
            {
                opts.DistributedEncodingSigningKey = "32-byte-signing-key-for-cluster!";
                // No CoordinatorUrl → this IS the coordinator.
                opts.WorkerRegistryPath = Path.Combine(tempDir, "workers.json");
            });

            IRemoteWorkerRegistry registry = provider.GetRequiredService<IRemoteWorkerRegistry>();

            registry.Should().BeOfType<JsonRemoteWorkerRegistry>();
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void WorkerDispatcher_IsAlwaysRemoteDispatcher()
    {
        // The dispatcher is the public face — always the remote wrapper,
        // which falls through to LocalWorkerDispatcher when no remote
        // workers are registered. This shape is the migration path for
        // adding remote workers without changing the encoder call site.
        ServiceProvider provider = BuildProvider();
        IWorkerDispatcher dispatcher = provider.GetRequiredService<IWorkerDispatcher>();

        dispatcher.Should().BeOfType<RemoteWorkerDispatcher>();
    }

    [Fact]
    public void DistributionServices_AllResolve()
    {
        ServiceProvider provider = BuildProvider();

        provider.GetRequiredService<ITaskSerializer>().Should().NotBeNull();
        provider.GetRequiredService<ISourceFetcher>().Should().BeOfType<HttpSourceFetcher>();
        provider.GetRequiredService<ITaskProgressSink>().Should().BeOfType<HttpTaskProgressSink>();
        provider.GetRequiredService<ITaskProgressStore>().Should().NotBeNull();
        provider.GetRequiredService<IWorkerAssigner>().Should().NotBeNull();
    }

    [Fact]
    public void BundleServices_AllResolve()
    {
        ServiceProvider provider = BuildProvider();

        provider.GetRequiredService<IMediaBlueprintBuilder>().Should().NotBeNull();
        provider.GetRequiredService<IMediaBlueprintWriter>().Should().NotBeNull();
        provider.GetRequiredService<IBundleGarbageCollector>().Should().NotBeNull();
    }

    [Fact]
    public void DeviceCapabilityServices_AreSingleton()
    {
        // DeviceCapabilityRegistry caches per-device negotiation results;
        // resolution must always return the same instance for the cache
        // to survive across pipeline invocations.
        ServiceProvider provider = BuildProvider();
        IDeviceCapabilityRegistry first = provider.GetRequiredService<IDeviceCapabilityRegistry>();
        IDeviceCapabilityRegistry second = provider.GetRequiredService<IDeviceCapabilityRegistry>();

        first.Should().BeSameAs(second);
        provider.GetRequiredService<IDeviceAwareVariantSelector>().Should().NotBeNull();
    }

    [Fact]
    public void ContentIntelligenceServices_AllResolve()
    {
        // OCR / Whisper / Crop / Intro — pipeline plug-ins; missing any one
        // silently disables a feature.
        ServiceProvider provider = BuildProvider();

        provider.GetRequiredService<ITesseractModelManager>().Should().NotBeNull();
        provider.GetRequiredService<ISubtitleOcrEngine>().Should().NotBeNull();
        provider.GetRequiredService<ISubtitleRouter>().Should().NotBeNull();
        provider.GetRequiredService<IWhisperTranscriber>().Should().NotBeNull();
        provider.GetRequiredService<ICropDetector>().Should().NotBeNull();
        provider
            .GetRequiredService<NoMercy.Encoder.ContentAnalysis.Segments.Probes.ISeasonSegmentPipeline>()
            .Should()
            .NotBeNull();
    }

    [Fact]
    public void DrmProcessors_BothRegistered()
    {
        // Aes128HlsDrmProcessor resolves via IDrmProcessor for the encode
        // pipeline; CencDrmProcessor is resolved directly for post-encode
        // DASH packaging (PrepareAsync is a no-op for CENC).
        ServiceProvider provider = BuildProvider();
        IEnumerable<NoMercy.Encoder.BuildingBlocks.Drm.IDrmProcessor> drm =
            provider.GetServices<NoMercy.Encoder.BuildingBlocks.Drm.IDrmProcessor>();
        drm.Should().NotBeEmpty();
        provider
            .GetRequiredService<NoMercy.Encoder.BuildingBlocks.Drm.CencDrmProcessor>()
            .Should()
            .NotBeNull();
    }

    [Fact]
    public void NotificationDispatcher_IsWebhookByDefault()
    {
        ServiceProvider provider = BuildProvider();
        INotificationDispatcher dispatcher = provider.GetRequiredService<INotificationDispatcher>();
        dispatcher.Should().BeOfType<WebhookNotificationDispatcher>();
    }
}
