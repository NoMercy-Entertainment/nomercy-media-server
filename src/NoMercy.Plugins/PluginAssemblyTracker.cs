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

namespace NoMercy.PluginSdk;

/// <summary>
/// Whether a plugin's files can be replaced yet.
/// <para>
/// The question an owner is really asking before an update or an uninstall is
/// not "did the load context unload" — it is "can these files be changed". The
/// first is a proxy for the second, and the second can simply be measured: open
/// the assembly for writing with no sharing and see whether the operating
/// system allows it.
/// </para>
/// <para>
/// Measuring it directly is also the only way to answer without forcing a
/// garbage collection. Weak-reference-plus-collect is the documented way to
/// observe an unload, and <c>GC.Collect</c> is banned in this codebase for a
/// good reason: it stops every thread, which on a media server means playback
/// stutters. A file-lock probe costs one failed open.
/// </para>
/// <para>
/// On Linux nothing locks, so the answer is that the file is replaceable, which
/// is true — a running plugin there can be overwritten and the change takes
/// effect on the next start.
/// </para>
/// </summary>
public interface IPluginAssemblyTracker
{
    /// <summary>Records where a plugin's assembly lives, at unload time.</summary>
    void TrackUnload(Ulid pluginId, string? assemblyPath);

    /// <summary>
    /// Whether anything of <paramref name="pluginId"/> still holds its files.
    /// <para>False for a plugin never tracked, and false once its files are
    /// gone: in both cases there is nothing left to be blocked on.</para>
    /// </summary>
    bool IsStillLoaded(Ulid pluginId);
}

public class PluginAssemblyTracker : IPluginAssemblyTracker
{
    private readonly ConcurrentDictionary<Ulid, string> _unloading = new();

    public void TrackUnload(Ulid pluginId, string? assemblyPath)
    {
        if (!string.IsNullOrWhiteSpace(assemblyPath))
            _unloading[pluginId] = assemblyPath;
    }

    public bool IsStillLoaded(Ulid pluginId)
    {
        if (!_unloading.TryGetValue(pluginId, out string? assemblyPath))
            return false;

        if (!File.Exists(assemblyPath))
        {
            // Already deleted, so nothing is holding it. Stop tracking.
            _unloading.TryRemove(pluginId, out _);
            return false;
        }

        if (IsLocked(assemblyPath))
            return true;

        _unloading.TryRemove(pluginId, out _);
        return false;
    }

    /// <summary>
    /// Whether the file cannot be opened for writing exclusively, which is
    /// exactly the condition that makes replacing or deleting it fail.
    /// </summary>
    private static bool IsLocked(string path)
    {
        try
        {
            using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None
            );
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            // Windows reports a still-resident assembly this way rather than as
            // an IOException, which is the same distinction the uninstall path
            // already had to learn.
            return true;
        }
    }
}
