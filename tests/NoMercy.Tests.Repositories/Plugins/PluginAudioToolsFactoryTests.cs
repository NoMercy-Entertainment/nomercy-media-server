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

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NoMercy.Data.Plugins;
using NoMercy.Database;
using NoMercy.Encoder.Composition;
using NoMercy.Encoder.Infrastructure;
using NoMercy.Encoder.Startup;
using NoMercy.MediaProcessing.DerivedAudio;
using NoMercy.PluginSdk;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Quotas;
using NoMercy.Storage;
using NoMercy.Storage.Drivers.Local;

namespace NoMercy.Tests.Repositories.Plugins;

/// <summary>
/// The factory takes its derived-audio storage through
/// <c>[FromKeyedServices("derived-audio")]</c>, which no test that news it up
/// by hand can get wrong - and no test that news it up by hand can prove
/// right either. A keyed parameter that the container cannot satisfy throws
/// only at the moment a plugin asks for its audio tools, which on a real
/// server is the middle of an unattended sweep. So this resolves the real
/// type out of a real container.
/// </summary>
public class PluginAudioToolsFactoryTests
{
    private static ServiceProvider BuildProvider()
    {
        ServiceCollection services = new();

        services.AddSingleton(new EncoderOptions { FfmpegPathOverride = "ffmpeg" });
        services.AddSingleton(new Mock<IProcessRunner>().Object);
        services.AddSingleton(new Mock<IStorage>().Object);
        services.AddSingleton(new Mock<IStorageDriver>().Object);
        services.AddKeyedSingleton("derived-audio", new Mock<IStorage>().Object);
        services.AddSingleton(new Mock<IDerivedAudioStore>().Object);
        services.AddSingleton(new Mock<IFfmpegCapabilityProbe>().Object);
        services.AddSingleton(new Mock<IPluginMusicAnalysisWriterFactory>().Object);
        services.AddSingleton(new Mock<IDbContextFactory<MediaContext>>().Object);
        services.AddSingleton<ILogger<PluginAudioTools>>(NullLogger<PluginAudioTools>.Instance);

        services.AddSingleton<IPluginAudioToolsFactory, PluginAudioToolsFactory>();

        return services.BuildServiceProvider();
    }

    [Fact]
    public void TheRealFactory_ResolvesAndBuildsToolsForAPlugin()
    {
        using ServiceProvider provider = BuildProvider();

        IPluginAudioToolsFactory factory = provider.GetRequiredService<IPluginAudioToolsFactory>();

        IPluginAudioTools tools = factory.CreateFor(Ulid.NewUlid(), "data");

        tools.Should().NotBeNull();
    }

    /// <summary>
    /// The disk meter is an optional parameter, which is exactly the kind a
    /// factory can forget to hand on: every test that builds the tools by
    /// hand would stay green while no render on a real server was ever
    /// counted. So this renders through tools the real factory built, out of
    /// a container that holds the meter, and expects the quota to bite.
    /// </summary>
    [Fact]
    public async Task TheRealFactory_HandsTheDiskMeterToTheToolsItBuilds()
    {
        string dataFolder = Path.Combine(Path.GetTempPath(), $"nm-factory-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataFolder);
        File.WriteAllBytes(Path.Combine(dataFolder, "in.wav"), new byte[16]);

        try
        {
            Mock<IStorage> storage = new();
            storage.Setup(s => s.Exists(It.IsAny<string>())).Returns(true);

            Mock<IProcessRunner> runner = new();
            runner
                .Setup(r =>
                    r.RunAsync(
                        It.IsAny<string>(),
                        It.IsAny<string[]>(),
                        It.IsAny<Action<string>?>(),
                        It.IsAny<Action<string>?>(),
                        It.IsAny<string?>(),
                        It.IsAny<CancellationToken>(),
                        It.IsAny<CancellationToken>(),
                        It.IsAny<Action<int>?>()
                    )
                )
                .Returns(
                    (
                        string _,
                        string[] arguments,
                        Action<string>? _,
                        Action<string>? _,
                        string? _,
                        CancellationToken _,
                        CancellationToken _,
                        Action<int>? _
                    ) =>
                    {
                        // ffmpeg's part: a kilobyte into the one output.
                        File.WriteAllBytes(arguments[^1], new byte[1024]);
                        return Task.FromResult(
                            new ProcessResult(0, string.Empty, string.Empty, TimeSpan.Zero)
                        );
                    }
                );

            ServiceCollection services = new();
            services.AddSingleton(new EncoderOptions { FfmpegPathOverride = "ffmpeg" });
            services.AddSingleton(runner.Object);
            services.AddSingleton(storage.Object);
            services.AddSingleton<IStorageDriver>(new LocalStorageDriver());
            services.AddKeyedSingleton("derived-audio", new Mock<IStorage>().Object);
            services.AddSingleton(new Mock<IDerivedAudioStore>().Object);
            services.AddSingleton(new Mock<IFfmpegCapabilityProbe>().Object);
            services.AddSingleton(new Mock<IPluginMusicAnalysisWriterFactory>().Object);
            services.AddSingleton(new Mock<IDbContextFactory<MediaContext>>().Object);
            services.AddSingleton<ILogger<PluginAudioTools>>(NullLogger<PluginAudioTools>.Instance);
            services.AddSingleton(new PluginQuotaMeter(new HalfAKilobyte(), TimeProvider.System));
            services.AddSingleton<IPluginAudioToolsFactory, PluginAudioToolsFactory>();
            await using ServiceProvider provider = services.BuildServiceProvider();

            IPluginAudioTools tools = provider
                .GetRequiredService<IPluginAudioToolsFactory>()
                .CreateFor(Ulid.NewUlid(), dataFolder);

            PluginAudioRunResult result = await tools.RenderAsync(
                new(
                    [new(PluginAudioInput.PluginFile("in.wav"))],
                    "[0:a]anull[out]",
                    [new("out", "out.wav", PluginAudioCodec.Wav)]
                ),
                null,
                null,
                new(null)
            );

            result.Refusal.Should().StartWith("the render does not fit the plugin's disk quota.");
            File.Exists(Path.Combine(dataFolder, "out.wav")).Should().BeFalse();
        }
        finally
        {
            Directory.Delete(dataFolder, recursive: true);
        }
    }

    private sealed class HalfAKilobyte : IPluginQuotaSource
    {
        public PluginQuota For(Ulid pluginId) => new(50, 512 * 1024 * 1024, 512, long.MaxValue);
    }

    /// <summary>
    /// The one-ffmpeg-at-a-time guard is a field on the instance, so a second
    /// call for the same plugin has to hand back the same instance - a guard a
    /// caller can get a fresh copy of guards nothing.
    /// </summary>
    [Fact]
    public void TheRealFactory_CachesOneInstancePerPlugin()
    {
        using ServiceProvider provider = BuildProvider();

        IPluginAudioToolsFactory factory = provider.GetRequiredService<IPluginAudioToolsFactory>();
        Ulid pluginId = Ulid.NewUlid();

        IPluginAudioTools first = factory.CreateFor(pluginId, "data");
        IPluginAudioTools second = factory.CreateFor(pluginId, "data");
        IPluginAudioTools other = factory.CreateFor(Ulid.NewUlid(), "data");

        second.Should().BeSameAs(first);
        other.Should().NotBeSameAs(first);
    }
}
