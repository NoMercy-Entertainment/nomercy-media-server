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

using System.Text.Json;
using NoMercy.Plugins.Abstractions;
using NoMercy.Plugins.Ipc;

namespace NoMercy.PluginHost;

/// <summary>
/// One member name to one entry point, by hand.
/// <para>
/// A switch rather than reflection over the method name: reflection would call
/// whatever a caller could name, and the set of things a plugin may be asked
/// to do is exactly the set of entry-point interfaces the contract declares.
/// </para>
/// </summary>
public static class PluginDispatch
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<string> InvokeAsync(
        IPlugin plugin,
        string member,
        string payloadJson,
        CancellationToken cancellationToken
    )
    {
        switch (member)
        {
            case nameof(IUiPlugin.GetViewAsync) when plugin is IUiPlugin ui:
            {
                PluginViewRequest request =
                    JsonSerializer.Deserialize<PluginViewRequest>(payloadJson, Json)
                    ?? throw new PluginRefusedException(NotUnderstood(member));

                PluginView view = await ui.GetViewAsync(request, cancellationToken);

                return JsonSerializer.Serialize(view, Json);
            }

            case nameof(IScheduledTaskPlugin.ExecuteAsync) when plugin is IScheduledTaskPlugin job:
            {
                ScheduledCall call =
                    JsonSerializer.Deserialize<ScheduledCall>(payloadJson, Json) ?? new(null);

                if (string.IsNullOrWhiteSpace(call.JobName))
                    await job.ExecuteAsync(cancellationToken);
                else
                    await job.ExecuteAsync(call.JobName, cancellationToken);

                return "{}";
            }

            default:
                throw new PluginRefusedException(NotUnderstood(member));
        }
    }

    private sealed record ScheduledCall(string? JobName);

    private static PluginRefusal NotUnderstood(string member) =>
        new(
            PluginRefusalCodes.HostServicesRemoved,
            "unknown plugin",
            $"The server asked this plugin for {member}.",
            "The plugin does not implement the entry point that member belongs to, so there is nothing here to call.",
            "Declare the hook in the plugin's manifest and implement its interface, or stop the server calling it. Docs: /nomercy-plugins/handbook/runtime-and-isolation",
            PluginRefusalSeverity.Blocked
        );
}
