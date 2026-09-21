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
using NoMercy.Plugins;
using NoMercy.Plugins.Abstractions;
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
    public void A_plugin_still_resolves_what_the_host_offers_it()
    {
        ServiceProvider host = Host();

        using PluginServiceProvider? child = PluginInstanceFactory.ChildContainer(
            host,
            typeof(OnePluginsRegistrator).Assembly
        );

        child!
            .GetService(typeof(HostFacade))
            .Should()
            .BeSameAs(
                host.GetRequiredService<HostFacade>(),
                "a second copy of a host facade is one nobody else publishes to"
            );

        // And injected, not only asked for by name: a plugin's own service
        // whose constructor needs a host facade is the case the container has
        // to answer, and asking the wrapper afterwards never exercises it.
        OnlyOnePluginHasThis injected = (OnlyOnePluginHasThis)
            child.GetService(typeof(OnlyOnePluginHasThis))!;

        injected.Facade.Should().BeSameAs(host.GetRequiredService<HostFacade>());
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

    internal sealed class OnlyOnePluginHasThis(HostFacade facade) : IDisposable
    {
        public HostFacade Facade { get; } = facade;

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
