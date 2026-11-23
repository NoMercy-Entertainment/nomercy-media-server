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
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NoMercy.Events;
using NoMercy.NmSystem.Logging;
using NoMercy.PluginSdk;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.OutOfProcess;
using NoMercy.PluginSdk.Revocation;
using NoMercy.PluginSdk.Verification;
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

        ServiceProvider provider = BuildProvider(services);
        IPluginManager manager1 = provider.GetRequiredService<IPluginManager>();
        IPluginManager manager2 = provider.GetRequiredService<IPluginManager>();

        manager1.Should().NotBeNull();
        manager1.Should().BeSameAs(manager2);
        manager1.Should().BeOfType<PluginManager>();

        (manager1 as IDisposable)?.Dispose();
    }

    [Fact]
    public void AddPluginSystem_AStaleRevocationListOnlyWarnsByDefault()
    {
        ServiceCollection services = new();
        services.AddSingleton<IEventBus, InMemoryEventBus>();
        services.AddLogging();
        services.AddSingleton(TestStorageHelper.CreateBackend());
        services.AddPluginSystem(_tempPluginsDir);
        ServiceProvider provider = BuildProvider(services);

        PluginRevocationGate gate = new(
            new StaleRevocationStore(),
            TimeProvider.System,
            provider.GetRequiredService<PluginTrustMode>()
        );

        gate.Check(Ulid.NewUlid(), packageHash: null, new(1, 0, 0))
            .Should()
            .BeNull("only official marketplace signatures are enforced by default");
    }

    /// <summary>
    /// The registered gate asks where a plugin runs before it asks about
    /// consent. An enable skips the consent refusal, so asked the other way
    /// round an unconsented plugin that cannot run in its own process would be
    /// started there anyway (#68).
    /// </summary>
    [Fact]
    public void AddPluginSystem_TheRunGateAsksWhereAPluginRunsBeforeItAsksAboutConsent()
    {
        ServiceCollection services = new();
        services.AddSingleton<IEventBus, InMemoryEventBus>();
        services.AddLogging();
        services.AddSingleton(TestStorageHelper.CreateBackend());
        services.AddSingleton<IPluginRemoteLoader>(new EverythingElsewhere());
        services.AddPluginSystem(_tempPluginsDir);
        ServiceProvider provider = BuildProvider(services);

        PluginRefusal? refusal = provider
            .GetRequiredService<IPluginRunGate>()
            .MayRun(
                new PluginInfo
                {
                    Id = Ulid.NewUlid(),
                    Name = "Analyser",
                    Description = "d",
                    Version = new(1, 0, 0),
                    Status = PluginStatus.Disabled,
                    Capabilities = new() { Hooks = [PluginHookCapability.AudioTools] },
                }
            );

        refusal!.Code.Should().Be(PluginRefusalCodes.OutOfProcessUnsupported);
        refusal.Why.Should().Contain(nameof(IPluginContext.AudioTools));
    }

    private sealed class EverythingElsewhere : IPluginRemoteLoader
    {
        public PluginIsolation IsolationFor(Ulid pluginId) => PluginIsolation.OutOfProcess;

        public Task<IPlugin?> LoadAsync(
            PluginDescription description,
            CancellationToken ct = default
        ) => Task.FromResult<IPlugin?>(null);
    }

    private sealed class StaleRevocationStore : IPluginRevocationStore
    {
        public PluginRevocationList Current => new(DateTimeOffset.UtcNow.AddDays(-30), []);

        public void Save(PluginRevocationList replacement) { }
    }

    [Fact]
    public void AddPluginSystem_TrustsTheShippedKeysAndOnlyWarnsByDefault()
    {
        ServiceCollection services = new();
        services.AddSingleton<IEventBus, InMemoryEventBus>();
        services.AddLogging();
        services.AddSingleton(TestStorageHelper.CreateBackend());

        services.AddPluginSystem(_tempPluginsDir);

        ServiceProvider provider = BuildProvider(services);

        provider
            .GetRequiredService<IPluginTrustedKeys>()
            .Find("mk_2026_09c")
            .Should()
            .Be("UbpdyF8xAjmWCWF0YWEOhfXO6OIUBcr39VsNw8jMiKQ=");
        provider.GetRequiredService<PluginTrustMode>().Enforce.Should().BeFalse();
        services
            .Should()
            .Contain(descriptor =>
                descriptor.ServiceType == typeof(IHostedService)
                && descriptor.ImplementationType == typeof(PluginTrustRefreshService)
            );
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

        ServiceProvider provider = BuildProvider(services);
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
        ServiceProvider provider = BuildProvider(services);
        ITestService service = provider.GetRequiredService<ITestService>();
        service.Should().NotBeNull();
        service.Should().BeOfType<TestService>();
    }

    /// <summary>
    /// <see cref="PluginServiceCollectionExtensions.AddPluginSystem" />'s
    /// registration of <see cref="IPluginContextFactory" /> must fetch every
    /// optional facade from the container with <c>GetService</c>, not leave it
    /// at its constructor default - otherwise a plugin never sees a facade the
    /// host registered, encoder and jobs included, not just the audio tools.
    /// </summary>
    [Fact]
    public void DiBuiltFactory_HandsDeclaredFacadesToThePlugin()
    {
        Mock<IPluginAudioToolsFactory> audioToolsFactory = new();
        IPluginAudioTools audioTools = Mock.Of<IPluginAudioTools>();
        audioToolsFactory
            .Setup(f => f.CreateFor(It.IsAny<Ulid>(), It.IsAny<string>()))
            .Returns(audioTools);

        ServiceCollection services = new();
        services.AddSingleton<IEventBus, InMemoryEventBus>();
        services.AddLogging();
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        services.AddSingleton(TestStorageHelper.CreateBackend());
        services.AddSingleton(Mock.Of<IPluginEncoder>());
        services.AddSingleton(Mock.Of<IPluginJobs>());
        services.AddSingleton(Mock.Of<IPluginMusicQuery>());
        services.AddSingleton(audioToolsFactory.Object);

        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        services.AddPluginSystem(_tempPluginsDir);

        ServiceProvider provider = BuildProvider(services);
        IPluginContextFactory factory = provider.GetRequiredService<IPluginContextFactory>();

        PluginCapabilities capabilities = new() { Hooks = ["encoder", "storage", "audioTools"] };

        IPluginContext context = factory.Create(
            Ulid.NewUlid(),
            _tempPluginsDir,
            NullLogger.Instance,
            capabilities
        );

        context.Music.Should().NotBeNull();
        context.Encoder.Should().NotBeNull();
        context.AudioTools.Should().NotBeNull();
    }

    /// <summary>
    /// A host that never calls <c>AddPluginLibraryAccess</c> or wires the
    /// encoder facade still gets a working platform - the resolve must not
    /// throw, and every optional facade gates to null exactly as it would
    /// when the plugin never declared the hook.
    /// </summary>
    [Fact]
    public void DiBuiltFactory_WithoutFacadesStillCreatesAContext()
    {
        ServiceCollection services = new();
        services.AddSingleton<IEventBus, InMemoryEventBus>();
        services.AddLogging();
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        services.AddSingleton(TestStorageHelper.CreateBackend());

        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        services.AddPluginSystem(_tempPluginsDir);

        ServiceProvider provider = BuildProvider(services);
        IPluginContextFactory factory = provider.GetRequiredService<IPluginContextFactory>();

        PluginCapabilities capabilities = new() { Hooks = ["encoder", "storage", "audioTools"] };

        IPluginContext context = factory.Create(
            Ulid.NewUlid(),
            _tempPluginsDir,
            NullLogger.Instance,
            capabilities
        );

        context.Music.Should().BeNull();
        context.Encoder.Should().BeNull();
        context.AudioTools.Should().BeNull();
    }

    /// <summary>
    /// The whole chain as the server builds it: a plugin that loads logs
    /// through a logger named after its key, and that key carries its name.
    /// Before this, every plugin logged through the plugin manager's logger
    /// and its lines read as "App".
    /// </summary>
    [Fact]
    public void DiBuiltFactory_GivesALoadingPluginALoggerUnderItsOwnName()
    {
        CapturingLoggerProvider logs = new();
        ServiceCollection services = new();
        services.AddSingleton<IEventBus, InMemoryEventBus>();
        services.AddLogging(builder => builder.AddProvider(logs));
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        services.AddSingleton(TestStorageHelper.CreateBackend());

        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        services.AddPluginSystem(_tempPluginsDir);

        ServiceProvider provider = BuildProvider(services);
        IPluginContextFactory factory = provider.GetRequiredService<IPluginContextFactory>();
        Ulid plugin = Ulid.NewUlid();

        IPluginContext context = factory.Create(
            plugin,
            _tempPluginsDir,
            NullLogger.Instance,
            new PluginCapabilities(),
            "Echo"
        );
        context.Logger.LogInformation("Echo plugin initialized");

        logs.Lines.Should().Contain(($"plugin:{plugin}", "Echo plugin initialized"));
        LogCategories.ResolveSource($"plugin:{plugin}").DisplayName.Should().Be("Echo");
    }

    /// <summary>
    /// A facade this server build does not wire yet refuses and names itself,
    /// rather than answering null.
    /// <para>
    /// The members above answer null, and that taught an author to branch
    /// and then quietly do nothing: the plugin looked idle, the owner saw no
    /// error, and nobody learned the server was the old one.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("Storage")]
    [InlineData("Server")]
    public void An_unwired_v3_facade_refuses_and_names_itself(string member)
    {
        ServiceCollection services = new();
        services.AddSingleton<IEventBus, InMemoryEventBus>();
        services.AddLogging();
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        services.AddSingleton(TestStorageHelper.CreateBackend());

        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        services.AddPluginSystem(_tempPluginsDir);

        ServiceProvider provider = BuildProvider(services);
        IPluginContextFactory factory = provider.GetRequiredService<IPluginContextFactory>();

        IPluginContext context = factory.Create(
            Ulid.NewUlid(),
            _tempPluginsDir,
            NullLogger.Instance,
            new PluginCapabilities()
        );

        Action reaching = () => typeof(IPluginContext).GetProperty(member)!.GetValue(context);

        PluginRefusedException refused = reaching
            .Should()
            .Throw<TargetInvocationException>()
            .WithInnerException<PluginRefusedException>()
            .Which;

        refused.Refusal.Code.Should().Be(PluginRefusalCodes.ContractVersionMismatch);
        refused.Refusal.What.Should().Contain($"IPluginContext.{member}");
        refused.Refusal.Fix.Should().Contain("context.Server.Version");
    }

    /// <summary>
    /// Net, Process, Native and Browser left that list as each landed: AddPluginSystem
    /// wires them, so every host that calls it has working facades rather than
    /// ones that refuse by name.
    /// <para>
    /// Wired is not the same as permitted. Each still refuses on its own terms
    /// — the capability, the owner's grant list, the bundle signature — which
    /// is what the facade's own tests cover.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("Net")]
    [InlineData("Process")]
    [InlineData("Native")]
    [InlineData("Browser")]
    public void A_facade_the_plugin_system_wires_is_reachable(string member)
    {
        ServiceCollection services = new();
        services.AddSingleton<IEventBus, InMemoryEventBus>();
        services.AddLogging();
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        services.AddSingleton(TestStorageHelper.CreateBackend());

        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        services.AddPluginSystem(_tempPluginsDir);

        ServiceProvider provider = BuildProvider(services);
        IPluginContext context = provider
            .GetRequiredService<IPluginContextFactory>()
            .Create(Ulid.NewUlid(), _tempPluginsDir, NullLogger.Instance, new PluginCapabilities());

        typeof(IPluginContext).GetProperty(member)!.GetValue(context).Should().NotBeNull();
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public List<(string Category, string Message)> Lines { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Capturing(categoryName, Lines);

        public void Dispose() { }

        private sealed class Capturing(
            string category,
            List<(string Category, string Message)> lines
        ) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter
            )
            {
                lock (lines)
                    lines.Add((category, formatter(state, exception)));
            }
        }
    }

    private static ServiceProvider BuildProvider(ServiceCollection services)
    {
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        return services.BuildServiceProvider();
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
