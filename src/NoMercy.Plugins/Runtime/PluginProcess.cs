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

using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Capabilities;

namespace NoMercy.PluginSdk.Runtime;

/// <summary>
/// Running one of the binaries the owner approved.
/// <para>
/// Stage one is in-process: a child runs as the server's own user, with the
/// server's own rights. So the binary is the exact file the owner read on the
/// permissions page and never the first hit on PATH, and every child goes in
/// the resource ledger, because one that outlives the plugin that started it
/// is one nobody is left to stop.
/// </para>
/// </summary>
public class PluginProcess(
    Ulid pluginId,
    IPluginCapabilityBroker broker,
    IPluginApprovedBinaries approved,
    IPluginProcessStarter starter,
    IPluginResourceLedger ledger
) : IPluginProcess
{
    private readonly List<IPluginProcessHandle> _running = [];
    private readonly Lock _gate = new();

    public IReadOnlyList<IPluginProcessHandle> Running
    {
        get
        {
            lock (_gate)
            {
                _running.RemoveAll(handle => handle.HasExited);

                return [.. _running];
            }
        }
    }

    public Task<IPluginProcessHandle> SpawnAsync(
        string binary,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment = null,
        string? workingDirectory = null,
        CancellationToken ct = default
    )
    {
        // Asked without a scope: the capability says whether this plugin may
        // start anything at all, and WHICH binary is the owner's grant list
        // below. The manifest names a program; the grant records where it
        // lives, so the two are not the same string and checking one against
        // the other refuses every plugin.
        if (broker.Check(pluginId, PluginCapabilityNames.ProcessSpawn) is not null)
            throw new PluginRefusedException(
                PluginRefusalMessages.ProcessSpawnUndeclared(pluginId.ToString(), binary)
            );

        // The name is only a label; this is the file. A name the owner never
        // approved resolves to nothing, and nothing is what runs.
        string path =
            approved.PathFor(pluginId, binary)
            ?? throw new PluginRefusedException(
                PluginRefusalMessages.ProcessSpawnUndeclared(pluginId.ToString(), binary)
            );

        IPluginProcessHandle handle = starter.Start(
            new(path, arguments, environment, workingDirectory)
        );

        lock (_gate)
            _running.Add(handle);

        ledger.Track(pluginId, handle);

        return Task.FromResult(handle);
    }
}
