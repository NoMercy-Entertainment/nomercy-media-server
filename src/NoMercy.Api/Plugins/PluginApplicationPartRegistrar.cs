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

using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.Extensions.Logging;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Mvc;

namespace NoMercy.Api.Plugins;

/// <summary>
/// Attaches a plugin's controllers to MVC, and detaches them again.
/// <para>
/// The part manager is captured during service configuration, which is the only
/// time MVC hands one out, and parts are added afterwards as plugins load — a
/// plugin installed from the dashboard has no other moment to get its routes.
/// Each add and each remove triggers a descriptor refresh, so enabling and
/// disabling a plugin takes effect without a restart.
/// </para>
/// </summary>
public class PluginApplicationPartRegistrar(
    ApplicationPartManager partManager,
    PluginActionDescriptorChangeProvider changeProvider,
    ILogger<PluginApplicationPartRegistrar> logger
) : IPluginAssemblyCatalog
{
    private readonly ConcurrentDictionary<Ulid, AttachedPlugin> _attached = new();

    /// <summary>What the route convention needs to know about an attached part.</summary>
    private sealed record AttachedPlugin(Assembly Assembly, bool AllowsAnonymousRest);

    public Ulid? OwnerOf(Assembly assembly) => Find(assembly)?.Key;

    public bool AllowsAnonymousRest(Assembly assembly) =>
        Find(assembly)?.Value.AllowsAnonymousRest ?? false;

    private KeyValuePair<Ulid, AttachedPlugin>? Find(Assembly assembly)
    {
        foreach (KeyValuePair<Ulid, AttachedPlugin> entry in _attached)
        {
            if (ReferenceEquals(entry.Value.Assembly, assembly))
                return entry;
        }

        return null;
    }

    /// <summary>
    /// Attaches every active plugin that actually carries controllers. A plugin
    /// with none is skipped rather than added as an empty part.
    /// </summary>
    public void AttachAll(IPluginManager pluginManager)
    {
        bool changed = false;

        // The list is never null from the platform's own manager, but this runs
        // against whatever IPluginManager the host registered, and a boot step
        // must not be the thing that dies on someone else's implementation.
        foreach (PluginInfo info in pluginManager.GetInstalledPlugins() ?? [])
        {
            if (info.Status != PluginStatus.Active)
                continue;

            changed |= AttachPart(info, pluginManager);
        }

        if (changed)
            changeProvider.TriggerChange();
    }

    /// <summary>Attaches one plugin's controllers and refreshes the route table when it did.</summary>
    public bool Attach(PluginInfo info, IPluginManager pluginManager)
    {
        if (!AttachPart(info, pluginManager))
            return false;

        changeProvider.TriggerChange();
        return true;
    }

    private bool AttachPart(PluginInfo info, IPluginManager pluginManager)
    {
        if (_attached.ContainsKey(info.Id))
            return false;

        if (info.Capabilities?.Rest != true)
            return false;

        Assembly? assembly = pluginManager.GetPluginInstance(info.Id)?.GetType().Assembly;

        if (assembly is null || !CarriesControllers(assembly))
            return false;

        partManager.ApplicationParts.Add(new AssemblyPart(assembly));
        _attached[info.Id] = new(assembly, info.Capabilities?.RestAnonymous ?? false);

        logger.LogInformation(
            "Attached controllers from plugin {PluginName} ({PluginId}).",
            info.Name,
            info.Id
        );

        return true;
    }

    public void Detach(Ulid pluginId)
    {
        if (!_attached.TryRemove(pluginId, out AttachedPlugin? attached))
            return;

        ApplicationPart? part = partManager.ApplicationParts.FirstOrDefault(candidate =>
            candidate is AssemblyPart assemblyPart
            && ReferenceEquals(assemblyPart.Assembly, attached.Assembly)
        );

        if (part is not null)
            partManager.ApplicationParts.Remove(part);

        changeProvider.TriggerChange();
    }

    /// <summary>
    /// Whether the assembly defines anything deriving from
    /// <see cref="PluginControllerBase"/>. A plugin whose types cannot all be
    /// loaded still counts on the ones that could — a missing optional
    /// dependency should cost that plugin one controller, not all of them.
    /// </summary>
    private static bool CarriesControllers(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes().Any(IsPluginController);
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types.Any(type => type is not null && IsPluginController(type));
        }
    }

    private static bool IsPluginController(Type type) =>
        typeof(PluginControllerBase).IsAssignableFrom(type) && type is { IsAbstract: false };
}
