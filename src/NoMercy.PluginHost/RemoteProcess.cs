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

using NoMercy.Plugins.Abstractions;
using NoMercy.Plugins.Ipc;

namespace NoMercy.PluginHost;

/// <summary>
/// Child processes, decided by the server and started by the plugin.
/// <para>
/// The server answers which file may run and never runs it. A process the
/// server started would be a child of the server, holding the server's rights
/// and sitting beside the sandbox rather than inside it. Started here it is a
/// child of the confined plugin process, so the job object, cgroup or sandbox
/// profile that already holds the plugin holds the child too, with nothing
/// extra to configure and nothing to get wrong per platform.
/// </para>
/// </summary>
public sealed class RemoteProcess(
    Ulid pluginId,
    RemoteCall call,
    PluginHostLaunch launch,
    ILocalProcessStarter starter
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

    public async Task<IPluginProcessHandle> SpawnAsync(
        string binary,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment = null,
        string? workingDirectory = null,
        CancellationToken ct = default
    )
    {
        PluginSpawnPermit permit =
            await call.AskAsync<PluginSpawnPermit>(
                "process",
                nameof(IPluginProcess.SpawnAsync),
                new { binary }
            )
            ?? throw new PluginRefusedException(
                PluginRefusalMessages.ProcessSpawnUndeclared(pluginId.ToString(), binary)
            );

        IPluginProcessHandle handle;

        try
        {
            handle = starter.Start(
                new LocalProcessRequest(
                    permit.ResolvedPath,
                    arguments,
                    environment,
                    workingDirectory ?? launch.DataFolder
                )
            );
        }
        catch (Exception exception) when (exception is not PluginRefusedException)
        {
            // The owner granted the binary and the start still failed, so what
            // refused is the confinement rather than the permission.
            throw new PluginRefusedException(
                PluginRefusalMessages.ProcessSpawnOutsideSandbox(pluginId.ToString(), binary)
            );
        }

        lock (_gate)
            _running.Add(handle);

        return handle;
    }
}
