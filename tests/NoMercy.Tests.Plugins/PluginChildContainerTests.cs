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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NoMercy.PluginSdk;
using NoMercy.PluginSdk.Abstractions;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// A plugin's services are its own. The host cannot see them, the plugin
/// beside it cannot see them, and they go when the plugin goes.
/// </summary>
public class PluginChildContainerTests
{
    private static ServiceProvider Host()
    {
        ServiceCollection services = new();
        services.AddSingleton<HostFacade>();
        services.AddSingleton<IPluginCallerAccessor>(NoPluginCaller.Instance);
        services.AddSingleton<IPluginManager, FakePluginManager>();
        services.AddLogging();
        services.Configure<HostSecretOptions>(options => options.Secret = "server-secret");
        services.AddSingleton(new PluginHostServiceCollection(services));

        return services.BuildServiceProvider();
    }

    [Fact]
    public void A_plugins_services_live_in_its_own_container()
    {
        ServiceProvider host = Host();

        using PluginServiceProvider? first = PluginInstanceFactory.ChildContainer(
            host,
            typeof(OnePluginsRegistrator).Assembly
        );

        first!.GetService(typeof(OnlyOnePluginHasThis)).Should().NotBeNull();
        host.GetService<OnlyOnePluginHasThis>()
            .Should()
            .BeNull("the host container never saw the plugin's registration");
    }

    [Fact]
    public void A_second_plugin_cannot_reach_the_first_plugins_services()
    {
        ServiceProvider host = Host();

        using PluginServiceProvider? first = PluginInstanceFactory.ChildContainer(
            host,
            typeof(OnePluginsRegistrator).Assembly
        );
        first!.GetService(typeof(OnlyOnePluginHasThis)).Should().NotBeNull();

        // A second plugin with no registrations of its own: the container it
        // would have shared is the host's, and the first plugin is not in it.
        host.GetService<OnlyOnePluginHasThis>().Should().BeNull();
    }

    [Fact]
    public void A_plugin_still_resolves_what_the_sdk_offers_it()
    {
        ServiceProvider host = Host();

        using PluginServiceProvider? child = PluginInstanceFactory.ChildContainer(
            host,
            typeof(OnePluginsRegistrator).Assembly
        );

        // IPluginCallerAccessor is declared in NoMercy.PluginSdk.Abstractions,
        // so the fallback may hand the host's own instance to the plugin.
        child!
            .GetService(typeof(IPluginCallerAccessor))
            .Should()
            .BeSameAs(
                host.GetRequiredService<IPluginCallerAccessor>(),
                "a second copy of an SDK facade is one nobody else publishes to"
            );

        // And injected, not only asked for by name: a plugin's own service
        // whose constructor needs an SDK facade is the case the container has
        // to answer, and asking the wrapper afterwards never exercises it.
        OnlyOnePluginHasThis injected = (OnlyOnePluginHasThis)
            child.GetService(typeof(OnlyOnePluginHasThis))!;

        injected.Caller.Should().BeSameAs(host.GetRequiredService<IPluginCallerAccessor>());
    }

    [Fact]
    public void A_host_internal_type_is_not_forwarded_to_a_plugin()
    {
        ServiceProvider host = Host();

        using PluginServiceProvider? child = PluginInstanceFactory.ChildContainer(
            host,
            typeof(OnePluginsRegistrator).Assembly
        );

        // HostFacade is declared in this test assembly, not in an SDK
        // assembly, so it stands in for a host internal a v3 plugin must not
        // reach through the fallback — only through a facade the SDK grants.
        child!
            .GetService(typeof(HostFacade))
            .Should()
            .BeNull("a host internal is not an SDK facade");
    }

    [Fact]
    public void A_logger_is_not_forwarded_to_a_plugin()
    {
        ServiceProvider host = Host();

        using PluginServiceProvider? child = PluginInstanceFactory.ChildContainer(
            host,
            typeof(OnePluginsRegistrator).Assembly
        );

        // ILogger<T> is Microsoft's, not the SDK's, and Logger<T> would need
        // the host's ILoggerFactory to build — another host internal. A
        // plugin's own service that needs one gets it from the plugin's own
        // registrations or not at all; nothing here does that for it.
        child!
            .GetService(typeof(ILogger<OnlyOnePluginHasThis>))
            .Should()
            .BeNull("logging infrastructure is a host internal, not an SDK facade");
    }

    [Fact]
    public void A_host_options_type_is_not_forwarded_to_a_plugin()
    {
        ServiceProvider host = Host();

        using PluginServiceProvider? child = PluginInstanceFactory.ChildContainer(
            host,
            typeof(OnePluginsRegistrator).Assembly
        );

        // IOptions<HostSecretOptions> closes over a type this test assembly
        // declares, standing in for the server's own configuration. Copying
        // the open IOptions<> registration would have let a plugin ask for
        // IOptions<AnyHostOptionsType> and read whatever the host configured.
        child!
            .GetService(typeof(IOptions<HostSecretOptions>))
            .Should()
            .BeNull("the server's own configuration is not an SDK facade");
    }

    [Fact]
    public void A_shared_sdk_service_the_broker_never_checks_is_not_forwarded_to_a_plugin()
    {
        ServiceProvider host = Host();

        using PluginServiceProvider? child = PluginInstanceFactory.ChildContainer(
            host,
            typeof(OnePluginsRegistrator).Assembly
        );

        // IPluginManager is declared in NoMercy.PluginSdk.Abstractions, so the
        // old assembly-prefix rule would have forwarded it — but nothing
        // behind it checks the calling plugin's identity before installing,
        // enabling or uninstalling ANY plugin. Only IPluginContext runs a
        // plugin's calls past the broker, so only what that route cannot
        // already give a plugin belongs in the explicit forwarded set.
        child!
            .GetService(typeof(IPluginManager))
            .Should()
            .BeNull("a shared host-wide service is not a plugin-scoped SDK facade");
    }

    [Fact]
    public void A_plugin_that_registers_nothing_gets_no_container()
    {
        ServiceProvider host = Host();

        PluginInstanceFactory
            .ChildContainer(host, typeof(string).Assembly)
            .Should()
            .BeNull("the common case allocates nothing");
    }

    [Fact]
    public void Disposing_the_container_disposes_what_the_plugin_registered()
    {
        ServiceProvider host = Host();

        PluginServiceProvider? child = PluginInstanceFactory.ChildContainer(
            host,
            typeof(OnePluginsRegistrator).Assembly
        );

        OnlyOnePluginHasThis service = (OnlyOnePluginHasThis)
            child!.GetService(typeof(OnlyOnePluginHasThis))!;

        child.Dispose();

        service
            .Disposed.Should()
            .BeTrue("it is disposed while its assembly is still loaded, or it is a crash later");
    }

    [Fact]
    public void Disposing_the_container_leaves_the_host_alone()
    {
        ServiceProvider host = Host();
        HostFacade facade = host.GetRequiredService<HostFacade>();

        PluginServiceProvider? child = PluginInstanceFactory.ChildContainer(
            host,
            typeof(OnePluginsRegistrator).Assembly
        );

        child!.Dispose();

        facade.Disposed.Should().BeFalse("the host's own service is not the plugin's to dispose");
        host.GetRequiredService<HostFacade>().Should().BeSameAs(facade);
    }

    internal sealed class HostFacade : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    /// <summary>Stands in for the server's own configuration, never a plugin's.</summary>
    internal sealed class HostSecretOptions
    {
        public string Secret { get; set; } = "";
    }

    /// <summary>
    /// A do-nothing stand-in: the test only asks whether the plugin container
    /// resolves this type, never calls a member on it.
    /// </summary>
    internal sealed class FakePluginManager : IPluginManager
    {
        public IReadOnlyList<PluginInfo> GetInstalledPlugins() => [];

        public Task InstallPluginAsync(string packageUrl, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task EnablePluginAsync(Ulid pluginId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DisablePluginAsync(Ulid pluginId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task UninstallPluginAsync(Ulid pluginId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<PluginLoadResult>> LoadAllAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();

        public IEnumerable<T> GetPluginsOfType<T>()
            where T : IPlugin => [];
    }

    internal sealed class OnlyOnePluginHasThis(IPluginCallerAccessor caller) : IDisposable
    {
        public IPluginCallerAccessor Caller { get; } = caller;

        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    /// <summary>
    /// What a plugin assembly carries. Found by type scan, so it lives in the
    /// test assembly the scan is pointed at.
    /// </summary>
    public sealed class OnePluginsRegistrator : IPluginServiceRegistrator
    {
        public void RegisterServices(IServiceCollection services) =>
            services.AddSingleton<OnlyOnePluginHasThis>();
    }
}
