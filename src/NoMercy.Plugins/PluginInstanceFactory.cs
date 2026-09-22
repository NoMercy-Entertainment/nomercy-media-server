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

        return new(own.BuildServiceProvider(), host);
    }

    /// <summary>
    /// Every service type the host offers, answered by asking the host for it.
    /// <para>
    /// A factory rather than a copy of the registration: a plugin resolving the
    /// event bus must get the server's one, not a second one nobody publishes
    /// to. Only what the plugin did not register itself, so a plugin may still
    /// replace a facade for its own use without replacing it for the server.
    /// </para>
    /// <para>
    /// Open generics are the exception. There is no closed type to ask the host
    /// for, so the registration is copied instead: <c>ILogger&lt;T&gt;</c> and
    /// the options types, which cost nothing to have twice.
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

            if (descriptor.ServiceType.IsGenericTypeDefinition)
            {
                own.Add(descriptor);
                continue;
            }

            Type serviceType = descriptor.ServiceType;
            own.AddSingleton(serviceType, _ => host.GetRequiredService(serviceType));
        }
    }
}
