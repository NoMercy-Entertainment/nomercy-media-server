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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NoMercy.Events;
using NoMercy.Plugins;
using NoMercy.Plugins.Abstractions;
using Xunit;

namespace NoMercy.Tests.Plugins;

public class PluginDiIntegrationTests : IDisposable
{
    private readonly string _tempPluginsDir;

    public PluginDiIntegrationTests()
    {
        _tempPluginsDir = Path.Combine(
            Path.GetTempPath(),
            "nomercy-di-tests-" + Ulid.NewUlid().ToString()
        );
        Directory.CreateDirectory(_tempPluginsDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempPluginsDir))
            {
                Directory.Delete(_tempPluginsDir, recursive: true);
            }
        }
        catch (IOException) { }
    }

    [Fact]
    public void AddPluginSystem_RegistersPluginManagerAsSingleton()
    {
        ServiceCollection services = new();
        services.AddSingleton<IEventBus, InMemoryEventBus>();
        services.AddLogging();
        services.AddSingleton(TestStorageHelper.CreateBackend());

        services.AddPluginSystem(_tempPluginsDir);

        ServiceProvider provider = services.BuildServiceProvider();
        IPluginManager manager1 = provider.GetRequiredService<IPluginManager>();
        IPluginManager manager2 = provider.GetRequiredService<IPluginManager>();

        manager1.Should().NotBeNull();
        manager1.Should().BeSameAs(manager2);
        manager1.Should().BeOfType<PluginManager>();

        (manager1 as IDisposable)?.Dispose();
    }

    [Fact]
    public void AddPluginSystem_NullServices_ThrowsArgumentNullException()
    {
        IServiceCollection? services = null;

        Action act = () => services!.AddPluginSystem("/tmp");

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void AddPluginSystem_NullPath_ThrowsArgumentException()
    {
        ServiceCollection services = new();

        Action act = () => services.AddPluginSystem(null!);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void AddPluginSystem_EmptyPath_ThrowsArgumentException()
    {
        ServiceCollection services = new();

        Action act = () => services.AddPluginSystem("");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void RegisterPluginServices_NullServices_ThrowsArgumentNullException()
    {
        IServiceCollection? services = null;
        InMemoryEventBus bus = new();
        PluginManager manager = new(
            bus,
            new MinimalServiceProvider(),
            NullLogger<PluginManager>.Instance,
            _tempPluginsDir,
            TestStorageHelper.CreateStorage(_tempPluginsDir),
            TestStorageHelper.CreateBackend()
        );

        Action act = () => services!.RegisterPluginServices(manager);

        act.Should().Throw<ArgumentNullException>();
        manager.Dispose();
    }

    [Fact]
    public void RegisterPluginServices_NullManager_ThrowsArgumentNullException()
    {
        ServiceCollection services = new();

        Action act = () => services.RegisterPluginServices(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void RegisterPluginServices_NoPlugins_DoesNothing()
    {
        ServiceCollection services = new();
        InMemoryEventBus bus = new();
        PluginManager manager = new(
            bus,
            new MinimalServiceProvider(),
            NullLogger<PluginManager>.Instance,
            _tempPluginsDir,
            TestStorageHelper.CreateStorage(_tempPluginsDir),
            TestStorageHelper.CreateBackend()
        );

        services.RegisterPluginServices(manager);

        services.Should().BeEmpty();
        manager.Dispose();
    }

    [Fact]
    public void GetServiceRegistrators_NoPlugins_ReturnsEmpty()
    {
        InMemoryEventBus bus = new();
        PluginManager manager = new(
            bus,
            new MinimalServiceProvider(),
            NullLogger<PluginManager>.Instance,
            _tempPluginsDir,
            TestStorageHelper.CreateStorage(_tempPluginsDir),
            TestStorageHelper.CreateBackend()
        );

        IEnumerable<IPluginServiceRegistrator> registrators = manager.GetServiceRegistrators();

        registrators.Should().BeEmpty();
        manager.Dispose();
    }

    [Fact]
    public void AddPluginSystem_ReturnsServiceCollectionForChaining()
    {
        ServiceCollection services = new();
        services.AddSingleton<IEventBus, InMemoryEventBus>();
        services.AddLogging();

        IServiceCollection result = services.AddPluginSystem(_tempPluginsDir);

        result.Should().BeSameAs(services);
    }

    [Fact]
    public void AddPluginSystem_ManagerGetsCorrectDependencies()
    {
        ServiceCollection services = new();
        InMemoryEventBus bus = new();
        services.AddSingleton<IEventBus>(bus);
        services.AddLogging();
        services.AddSingleton(TestStorageHelper.CreateBackend());

        services.AddPluginSystem(_tempPluginsDir);

        ServiceProvider provider = services.BuildServiceProvider();
        IPluginManager manager = provider.GetRequiredService<IPluginManager>();

        manager.Should().NotBeNull();
        manager.GetInstalledPlugins().Should().BeEmpty();

        (manager as IDisposable)?.Dispose();
    }

    [Fact]
    public void IPluginServiceRegistrator_CanRegisterServices()
    {
        TestServiceRegistrator registrator = new();
        ServiceCollection services = new();

        registrator.RegisterServices(services);

        services.Should().ContainSingle();
        ServiceProvider provider = services.BuildServiceProvider();
        ITestService service = provider.GetRequiredService<ITestService>();
        service.Should().NotBeNull();
        service.Should().BeOfType<TestService>();
    }

    /// <summary>
    /// <see cref="PluginServiceCollectionExtensions.AddPluginSystem" />'s
    /// registration of <see cref="IPluginContextFactory" /> must fetch every
    /// optional facade from the container with <c>GetService</c>, not leave it
    /// at its constructor default - otherwise a plugin never sees a facade the
    /// host registered, encoder and jobs included, not just the three analysis
    /// ones.
    /// </summary>
    [Fact]
    public void DiBuiltFactory_HandsDeclaredFacadesToThePlugin()
    {
        Mock<IPluginAudioToolsFactory> audioToolsFactory = new();
        Mock<IPluginMusicAnalysisWriterFactory> analysisWriterFactory = new();
        IPluginAudioTools audioTools = Mock.Of<IPluginAudioTools>();
        IPluginMusicAnalysisWriter analysisWriter = Mock.Of<IPluginMusicAnalysisWriter>();
        audioToolsFactory.Setup(f => f.CreateFor(It.IsAny<Ulid>())).Returns(audioTools);
        analysisWriterFactory.Setup(f => f.CreateFor(It.IsAny<Ulid>())).Returns(analysisWriter);

        ServiceCollection services = new();
        services.AddSingleton<IEventBus, InMemoryEventBus>();
        services.AddLogging();
        services.AddSingleton(TestStorageHelper.CreateBackend());
        services.AddSingleton(Mock.Of<IPluginEncoder>());
        services.AddSingleton(Mock.Of<IPluginJobs>());
        services.AddSingleton(Mock.Of<IPluginStorage>());
        services.AddSingleton(Mock.Of<IPluginMusicQuery>());
        services.AddSingleton(audioToolsFactory.Object);
        services.AddSingleton(Mock.Of<IPluginDerivedAudio>());
        services.AddSingleton(analysisWriterFactory.Object);

        services.AddPluginSystem(_tempPluginsDir);

        ServiceProvider provider = services.BuildServiceProvider();
        IPluginContextFactory factory = provider.GetRequiredService<IPluginContextFactory>();

        PluginCapabilities capabilities = new()
        {
            Hooks = ["encoder", "storage", "audioTools", "derivedAudio", "musicAnalysisWrite"],
        };

        IPluginContext context = factory.Create(
            Ulid.NewUlid(),
            _tempPluginsDir,
            NullLogger.Instance,
            capabilities
        );

        context.Music.Should().NotBeNull();
        context.Encoder.Should().NotBeNull();
        context.Storage.Should().NotBeNull();
        context.AudioTools.Should().NotBeNull();
        context.DerivedAudio.Should().NotBeNull();
        context.MusicAnalysisWriter.Should().NotBeNull();
    }

    /// <summary>
    /// A host that never calls <c>AddPluginLibraryAccess</c> or wires the
    /// encoder/storage facades still gets a working platform - the resolve
    /// must not throw, and every optional facade gates to null exactly as it
    /// would when the plugin never declared the hook.
    /// </summary>
    [Fact]
    public void DiBuiltFactory_WithoutFacadesStillCreatesAContext()
    {
        ServiceCollection services = new();
        services.AddSingleton<IEventBus, InMemoryEventBus>();
        services.AddLogging();
        services.AddSingleton(TestStorageHelper.CreateBackend());

        services.AddPluginSystem(_tempPluginsDir);

        ServiceProvider provider = services.BuildServiceProvider();
        IPluginContextFactory factory = provider.GetRequiredService<IPluginContextFactory>();

        PluginCapabilities capabilities = new()
        {
            Hooks = ["encoder", "storage", "audioTools", "derivedAudio", "musicAnalysisWrite"],
        };

        IPluginContext context = factory.Create(
            Ulid.NewUlid(),
            _tempPluginsDir,
            NullLogger.Instance,
            capabilities
        );

        context.Music.Should().BeNull();
        context.Encoder.Should().BeNull();
        context.Storage.Should().BeNull();
        context.AudioTools.Should().BeNull();
        context.DerivedAudio.Should().BeNull();
        context.MusicAnalysisWriter.Should().BeNull();
    }

    public interface ITestService
    {
        string GetValue();
    }

    private sealed class TestService : ITestService
    {
        public string GetValue() => "from-plugin";
    }

    private sealed class TestServiceRegistrator : IPluginServiceRegistrator
    {
        public void RegisterServices(IServiceCollection services)
        {
            services.AddSingleton<ITestService, TestService>();
        }
    }

    private sealed class MinimalServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
