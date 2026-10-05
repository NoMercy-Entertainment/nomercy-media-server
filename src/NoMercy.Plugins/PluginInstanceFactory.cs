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
using Microsoft.Extensions.DependencyInjection;
using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.PluginSdk;

/// <summary>
/// Creates plugin instances through a container rather than
/// <see cref="Activator"/>, so a plugin's constructor receives both what it
/// registered for itself and what the host offers it.
/// </summary>
internal static class PluginInstanceFactory
{
    internal static IPlugin Create(IServiceProvider services, Type pluginType)
    {
        return (IPlugin)ActivatorUtilities.CreateInstance(services, pluginType);
    }

    /// <summary>
    /// The plugin's own container, or null when it registered nothing — which
    /// is most plugins, and they allocate nothing for this.
    /// <para>
    /// Built from the assembly already loaded for the plugin. The old path
    /// loaded every plugin a second time, in a throwaway load context, before
    /// the host container existed; that is what made a service-contributing
    /// plugin fragile and what put its services in the host's container where
    /// every other plugin could reach them.
    /// </para>
    /// </summary>
    internal static PluginServiceProvider? ChildContainer(IServiceProvider host, Assembly assembly)
    {
        IServiceCollection own = new ServiceCollection();
        bool registered = false;

        foreach (
            Type registratorType in assembly
                .GetTypes()
                .Where(type =>
                    typeof(IPluginServiceRegistrator).IsAssignableFrom(type)
                    && type is { IsAbstract: false, IsInterface: false }
                )
        )
        {
            if (Activator.CreateInstance(registratorType) is not IPluginServiceRegistrator plugin)
                continue;

            plugin.RegisterServices(own);
            registered = true;
        }

        if (!registered || own.Count == 0)
            return null;

        AddHostFallback(own, host);

        return new(own.BuildServiceProvider());
    }

    /// <summary>
    /// The only types a service-registering plugin's own container may pull
    /// from the host. Every capability a plugin uses goes through
    /// <see cref="IPluginContext"/>, where the broker checks it against that
    /// plugin's grants; a type belongs here only when it has no such check to
    /// bypass. <see cref="IPluginCallerAccessor"/> qualifies because it is a
    /// read-only view of who is asking, not an action on anyone.
    /// <para>
    /// Being declared in the SDK does not make a type safe to hand out:
    /// <c>IPluginManager</c> is SDK-declared and checks no caller before it
    /// installs or removes a plugin. Adding a type here is a security decision.
    /// </para>
    /// </summary>
    private static readonly HashSet<Type> ForwardedToPlugins = [typeof(IPluginCallerAccessor)];

    /// <summary>
    /// Every service type in <see cref="ForwardedToPlugins"/>, answered by
    /// asking the host for it.
    /// <para>
    /// A factory rather than a copy of the registration: a plugin resolving a
    /// forwarded type must get the server's one, not a second one nobody
    /// publishes to. Only what the plugin did not register itself, so a
    /// plugin may still replace one for its own use without replacing it for
    /// the server.
    /// </para>
    /// <para>
    /// No open generic is forwarded: <c>IOptions&lt;T&gt;</c> closing over a
    /// host options type would hand a plugin the server's own configuration,
    /// and there is no closed type on an open generic to list here anyway.
    /// </para>
    /// <para>
    /// A plugin's own service that needs <c>ILogger&lt;T&gt;</c> gets nothing
    /// from here: <see cref="IPluginContext.Logger"/> is the sanctioned route,
    /// and a constructor asking for the Microsoft type instead fails with a
    /// clear DI error rather than reaching the host's factory.
    /// </para>
    /// </summary>
    private static void AddHostFallback(IServiceCollection own, IServiceProvider host)
    {
        if (host.GetService<PluginHostServiceCollection>() is not { } hostServices)
            return;

        HashSet<Type> taken = [.. own.Select(descriptor => descriptor.ServiceType)];

        foreach (ServiceDescriptor descriptor in hostServices.Services)
        {
            // A keyed service has no answer to a plain ask, so there is nothing
            // to delegate. A plugin that wants one registers it itself.
            if (descriptor.IsKeyedService)
                continue;

            if (!taken.Add(descriptor.ServiceType))
                continue;

            if (!ForwardedToPlugins.Contains(descriptor.ServiceType))
                continue;

            Type serviceType = descriptor.ServiceType;
            own.AddSingleton(serviceType, _ => host.GetRequiredService(serviceType));
        }
    }
}
