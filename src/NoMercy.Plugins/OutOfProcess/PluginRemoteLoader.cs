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

using Microsoft.Extensions.Logging;
using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.PluginSdk.OutOfProcess;

/// <summary>
/// The answer the loader asks for: start this plugin somewhere else, or say
/// you cannot.
/// <para>
/// Everything under here already existed and nothing joined it up. This is the
/// piece that turns the owner's isolation setting into a running process.
/// </para>
/// </summary>
public sealed class PluginRemoteLoader(
    PluginProcessSupervisor supervisor,
    IPluginHostExecutable executable,
    ILogger logger,
    Func<PluginRuntimeMode>? mode = null
) : IPluginRemoteLoader
{
    private readonly Func<PluginRuntimeMode> _mode = mode ?? (() => PluginRuntimeMode.Load());

    public PluginIsolation IsolationFor(Ulid pluginId) => _mode().For(pluginId);

    public async Task<IPlugin?> LoadAsync(
        PluginDescription description,
        CancellationToken ct = default
    )
    {
        // Said once, here, rather than thrown from the launcher. The loader's
        // answer to "cannot" is to run the plugin in this process, which is a
        // working plugin rather than none, and the dashboard is what tells the
        // owner their choice is not being honored.
        if (executable.Path is null)
        {
            logger.LogWarning(
                "Plugin {PluginId} is set to run in its own process and this install has no plugin host beside the server. Running it in the server instead.",
                [description.Id]
            );

            return null;
        }

        if (!await supervisor.StartAsync(description.Id, ct))
        {
            logger.LogWarning(
                "Plugin {PluginId} could not be started in its own process. Running it in the server instead.",
                [description.Id]
            );

            return null;
        }

        PluginHostChannel channel = new(description.Id, supervisor.TokenFor(description.Id));

        return new RemotePlugin(
            description,
            channel,
            async () =>
            {
                await supervisor.StopAsync(description.Id, CancellationToken.None);
                channel.Dispose();
            }
        );
    }
}
