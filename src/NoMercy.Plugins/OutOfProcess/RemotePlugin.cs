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
using NoMercy.PluginSdk.Ipc;

namespace NoMercy.PluginSdk.OutOfProcess;

/// <summary>
/// A plugin running somewhere else, as the registry holds it.
/// <para>
/// The registry, the dashboard and every screen ask an <see cref="IPlugin" />
/// for its name, id and version. A plugin in its own process is still one of
/// those here, so nothing above the loader has to know where the code runs.
/// </para>
/// <para>
/// The four describing members answer from the manifest rather than over the
/// channel. They are read while the dashboard lists plugins, including ones
/// whose process is stopped or crashed, and a round trip would make a stopped
/// plugin nameless on the very screen offering to start it.
/// </para>
/// </summary>
public sealed class RemotePlugin(
    PluginDescription description,
    IPluginHostService host,
    Func<Task>? stop = null
) : IPlugin
{
    public string Name => description.Name;

    public string Description => description.Description;

    public Ulid Id => description.Id;

    public Version Version => description.Version;

    /// <summary>
    /// Tells the other process to start, and does not hand it this context.
    /// <para>
    /// The context here belongs to the server's heap. The plugin already holds
    /// its own, built against the broker, and an object cannot cross a process
    /// boundary: passing this one would hand the plugin a reference into the
    /// server it is being isolated from.
    /// </para>
    /// </summary>
    public void Initialize(IPluginContext context)
    {
        PluginCallResponse response = host.InitializeAsync(
                new PluginCallRequest(Id.ToString(), "plugin", nameof(Initialize), "null", null)
            )
            .GetAwaiter()
            .GetResult();

        if (response.Ok)
        {
            return;
        }

        throw new PluginRefusedException(
            new PluginRefusal(
                response.Refusal?.Code ?? PluginRefusalCodes.HostServicesRemoved,
                Id.ToString(),
                response.Refusal?.What ?? "The server started this plugin in a process of its own.",
                response.Refusal?.Why ?? "The plugin process refused to initialize.",
                response.Refusal?.Fix
                    ?? "Read the server log around this line, then start the plugin again from its health page. Docs: /nomercy-plugins/handbook/runtime-and-isolation",
                PluginRefusalSeverity.Blocked
            )
        );
    }

    /// <summary>
    /// Asks the process to stop, then makes sure it did.
    /// <para>
    /// A plugin gets to run its own shutdown, because it may be holding a
    /// recording or a half-written import. Killed outright it would leave both
    /// behind. The kill still happens afterwards: a plugin that ignores the
    /// request does not get to outlive the server that started it.
    /// </para>
    /// </summary>
    public void Dispose()
    {
        try
        {
            host.ShutdownAsync(
                    new PluginCallRequest(Id.ToString(), "plugin", nameof(Dispose), "null", null)
                )
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception)
        {
            // The channel is already gone, which is the thing being asked for.
        }

        stop?.Invoke().GetAwaiter().GetResult();
    }
}

/// <summary>What the manifest says about a plugin, with no process needed to say it.</summary>
public sealed record PluginDescription(Ulid Id, string Name, string Description, Version Version);
