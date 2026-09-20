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
using System.Runtime.Loader;

namespace NoMercy.Plugins;

public class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;
    private readonly string _pluginDir;
    private readonly IReadOnlySet<string> _sharedAssemblies;

    // Named after the assembly it was built for, because a nameless context is
    // unidentifiable: every one of them reads as null in a memory dump and in
    // AssemblyLoadContext.All, so a context that outlives its plugin cannot be
    // told from any other. The path carries the shadow copy's id, so the name
    // is unique per load as well as per plugin.
    public PluginLoadContext(string pluginPath, IReadOnlySet<string>? sharedAssemblies = null)
        : base(name: pluginPath, isCollectible: true)
    {
        _resolver = new(pluginPath);
        _pluginDir =
            Path.GetDirectoryName(pluginPath)
            ?? throw new InvalidOperationException("Plugin directory could not be determined.");

        _sharedAssemblies = sharedAssemblies ?? PluginHostOptions.DefaultSharedAssemblies;
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // Shared assemblies → the host's own copy, by name. Returning null here
        // used to mean "let the default context's own resolution handle it", but
        // that resolution still matches by full AssemblyName including version —
        // so a plugin built against a newer host release than this one is
        // running (e.g. its manifest names NoMercy.Plugins.Abstractions 0.1.472
        // while this process loaded 0.1.404) failed to load at all, even though
        // the plugin declared itself ABI-compatible. A shared name is a promise
        // the plugin runs against whatever the host already has loaded, not a
        // specific build of it, so resolve it explicitly against the assemblies
        // already loaded into the default context and ignore the version the
        // plugin asked for.
        if (assemblyName.Name is not null && _sharedAssemblies.Contains(assemblyName.Name))
        {
            Assembly? alreadyLoaded = Default.Assemblies.FirstOrDefault(loaded =>
                string.Equals(loaded.GetName().Name, assemblyName.Name, StringComparison.Ordinal)
            );
            if (alreadyLoaded is not null)
                return alreadyLoaded;

            // Nothing in the host has needed this shared assembly yet, so it
            // isn't in Default.Assemblies to find. Load it into the default
            // context by its bare name (ignoring the plugin's requested
            // version, same promise as the already-loaded branch above) so
            // the host ends up with exactly one copy regardless of which
            // caller — host or plugin — happens to touch it first.
            return Default.LoadFromAssemblyName(new(assemblyName.Name));
        }

        // Framework assemblies → host resolves
        if (
            assemblyName.Name is not null
            && (
                assemblyName.Name.StartsWith("System.", StringComparison.Ordinal)
                || assemblyName.Name == "System.Private.CoreLib"
            )
        )
        {
            return null;
        }

        // Resolver first
        string? resolved = _resolver.ResolveAssemblyToPath(assemblyName);
        if (resolved is not null)
            return LoadFromAssemblyPath(resolved);

        // Fallback: plugin directory
        string candidate = Path.Combine(_pluginDir, assemblyName.Name + ".dll");
        if (File.Exists(candidate))
            return LoadFromAssemblyPath(candidate);

        return null;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        string? libraryPath = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        if (libraryPath is not null)
        {
            return LoadUnmanagedDllFromPath(libraryPath);
        }

        return IntPtr.Zero;
    }
}
