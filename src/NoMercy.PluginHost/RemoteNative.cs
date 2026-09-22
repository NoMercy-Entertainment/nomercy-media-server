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
using System.Runtime.InteropServices;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Ipc;

namespace NoMercy.PluginHost;

/// <summary>Loads a native library the server resolved, into this process.</summary>
public interface ILocalNativeLoader
{
    bool TryLoad(string path, out nint handle);
}

/// <summary>The real one.</summary>
public sealed class SystemLocalNativeLoader : ILocalNativeLoader
{
    public bool TryLoad(string path, out nint handle) => NativeLibrary.TryLoad(path, out handle);
}

/// <summary>
/// Asks which file, and loads it here.
/// <para>
/// The server resolves the name against the plugin's bundle and checks the
/// marketplace signature; this process performs the load, because a load in
/// the server's process lands in the server's load context where the plugin's
/// <c>DllImport</c> declarations never look, and runs with the server's rights
/// rather than inside the sandbox.
/// </para>
/// </summary>
public sealed class RemoteNative(Ulid pluginId, RemoteCall call, ILocalNativeLoader loader)
    : IPluginNative
{
    private readonly ConcurrentDictionary<string, nint> _loaded = new(StringComparer.Ordinal);

    public async Task LoadAsync(string library, CancellationToken ct = default)
    {
        if (_loaded.ContainsKey(library))
            return;

        PluginNativePermit permit =
            await call.AskAsync<PluginNativePermit>(
                "native",
                nameof(IPluginNative.LoadAsync),
                new { library }
            )
            ?? throw new PluginRefusedException(
                PluginRefusalMessages.FileOutsideGrant(pluginId.ToString(), library)
            );

        if (!loader.TryLoad(permit.ResolvedPath, out nint handle))
            throw new PluginRefusedException(
                PluginRefusalMessages.FileOutsideGrant(pluginId.ToString(), library)
            );

        _loaded[library] = handle;
    }

    public bool IsLoaded(string library) => _loaded.ContainsKey(library);
}
