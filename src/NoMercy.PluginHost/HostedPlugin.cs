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
using NoMercy.Plugins.Abstractions;

namespace NoMercy.PluginHost;

/// <summary>The one plugin this process exists to run.</summary>
public interface IHostedPlugin
{
    IPlugin Instance { get; }

    Task<string> InvokeAsync(
        string member,
        string payloadJson,
        CancellationToken cancellationToken
    );
}

/// <summary>
/// The plugin assembly, loaded into a context this process can drop.
/// <para>
/// Its own load context even here, where the process holds one plugin: an
/// update replaces the assembly while the server is running, and a context
/// that cannot be unloaded would hold the file open until a restart.
/// </para>
/// </summary>
public sealed class HostedPlugin : IHostedPlugin, IDisposable
{
    private readonly PluginHostLoadContext _loadContext;

    public IPlugin Instance { get; }

    public HostedPlugin(PluginHostLaunch launch, IPluginContext context)
    {
        _loadContext = new(launch.AssemblyPath);

        Assembly assembly = _loadContext.LoadFromAssemblyPath(launch.AssemblyPath);
        Type entry =
            assembly
                .GetTypes()
                .FirstOrDefault(type =>
                    typeof(IPlugin).IsAssignableFrom(type)
                    && type is { IsAbstract: false, IsInterface: false }
                )
            ?? throw new InvalidOperationException(
                $"{launch.AssemblyPath} carries no type implementing IPlugin"
            );

        Instance = (IPlugin)Activator.CreateInstance(entry)!;
        Instance.Initialize(context);
    }

    public Task<string> InvokeAsync(
        string member,
        string payloadJson,
        CancellationToken cancellationToken
    ) => PluginDispatch.InvokeAsync(Instance, member, payloadJson, cancellationToken);

    public void Dispose()
    {
        Instance.Dispose();
        _loadContext.Unload();
    }
}

/// <summary>
/// The plugin's own dependencies, with the contract shared from this process.
/// <para>
/// A plugin carrying its own copy of the abstractions would load a second
/// <c>IPlugin</c> type, and a cast between the two fails with a message that
/// names the same type twice.
/// </para>
/// </summary>
public sealed class PluginHostLoadContext(string assemblyPath)
    : AssemblyLoadContext(isCollectible: true)
{
    private readonly AssemblyDependencyResolver _resolver = new(assemblyPath);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (
            assemblyName.Name?.StartsWith("NoMercy.Plugins.Abstractions", StringComparison.Ordinal)
            == true
        )
            return null;

        string? path = _resolver.ResolveAssemblyToPath(assemblyName);

        return path is null ? null : LoadFromAssemblyPath(path);
    }
}
