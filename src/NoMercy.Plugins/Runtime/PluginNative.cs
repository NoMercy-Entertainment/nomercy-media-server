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
using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.PluginSdk.Runtime;

/// <summary>
/// Native code from the plugin's own bundle.
/// <para>
/// Gated on the marketplace signature and nothing else. Managed code can be
/// refused while it runs; native code cannot be inspected or stopped once it
/// is mapped into the process, so the only moment to say no is before it
/// loads, and the only thing worth asking is who built the bundle.
/// </para>
/// <para>
/// The name is a bare library name, never a path. The signature says the
/// bundle was built by the marketplace; it says nothing at all about a file
/// reached from outside it.
/// </para>
/// </summary>
public class PluginNative(
    Ulid pluginId,
    IPluginBundleSignature signature,
    INativeLibraryLoader loader,
    string pluginDirectory
) : IPluginNative
{
    private readonly ConcurrentDictionary<string, nint> _loaded = new(StringComparer.Ordinal);

    public Task LoadAsync(string library, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(library) || IsPath(library))
            throw new PluginRefusedException(
                PluginRefusalMessages.FileOutsideGrant(pluginId.ToString(), library)
            );

        if (!signature.IsMarketplaceSigned(pluginId))
            throw new PluginRefusedException(
                PluginRefusalMessages.NativeCodeUnsigned(pluginId.ToString(), library)
            );

        if (_loaded.ContainsKey(library))
            return Task.CompletedTask;

        string path = Path.Combine(pluginDirectory, FileName(library));

        if (!File.Exists(path) || !loader.TryLoad(path, out nint handle))
            throw new PluginRefusedException(
                PluginRefusalMessages.FileOutsideGrant(pluginId.ToString(), library)
            );

        _loaded[library] = handle;

        return Task.CompletedTask;
    }

    public bool IsLoaded(string library) => _loaded.ContainsKey(library);

    /// <summary>
    /// A separator or a <c>..</c> segment is a path, and a path is the one
    /// thing this member does not take. An absolute path on Windows also
    /// carries a colon, which neither of the others catches.
    /// </summary>
    private static bool IsPath(string library) =>
        library.Contains('/')
        || library.Contains('\\')
        || library.Contains("..")
        || library.Contains(':');

    /// <summary>
    /// The host picks the file for the platform it is on, so a plugin ships
    /// one name and does not branch on the operating system to use it.
    /// </summary>
    private static string FileName(string library)
    {
        if (OperatingSystem.IsWindows())
            return $"{library}.dll";

        return OperatingSystem.IsMacOS() ? $"lib{library}.dylib" : $"lib{library}.so";
    }
}
