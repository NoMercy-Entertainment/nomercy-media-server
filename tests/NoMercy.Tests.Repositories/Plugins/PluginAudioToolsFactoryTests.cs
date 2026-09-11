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
using NoMercy.Plugins;
using NoMercy.Plugins.Abstractions;
using NoMercy.Storage;

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

        IPluginAudioTools tools = factory.CreateFor(Ulid.NewUlid());

        tools.Should().NotBeNull();
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

        IPluginAudioTools first = factory.CreateFor(pluginId);
        IPluginAudioTools second = factory.CreateFor(pluginId);
        IPluginAudioTools other = factory.CreateFor(Ulid.NewUlid());

        second.Should().BeSameAs(first);
        other.Should().NotBeSameAs(first);
    }
}
