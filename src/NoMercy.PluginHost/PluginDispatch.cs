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

            case nameof(ISearchablePlugin.SearchAsync) when plugin is ISearchablePlugin searchable:
            {
                SearchCall call =
                    JsonSerializer.Deserialize<SearchCall>(payloadJson, Json)
                    ?? throw new PluginRefusedException(NotUnderstood(member));

                // No caller, no search. A plugin's results can be scoped to who
                // is asking, and inventing an identity here would hand one
                // user's results to whoever called without naming themselves.
                if (call.Caller is null)
                    throw new PluginRefusedException(NoCaller(member));

                IReadOnlyList<PluginSearchResult> results = await searchable.SearchAsync(
                    call.Query ?? string.Empty,
                    call.Caller,
                    cancellationToken
                );

                return JsonSerializer.Serialize(results, Json);
            }

            case nameof(IAuthPlugin.AuthenticateAsync) when plugin is IAuthPlugin auth:
            {
                AuthCall call =
                    JsonSerializer.Deserialize<AuthCall>(payloadJson, Json)
                    ?? throw new PluginRefusedException(NotUnderstood(member));

                AuthResult result = await auth.AuthenticateAsync(
                    call.Token ?? string.Empty,
                    cancellationToken
                );

                return JsonSerializer.Serialize(result, Json);
            }

            case nameof(IMediaSourcePlugin.ScanAsync) when plugin is IMediaSourcePlugin source:
            {
                ScanCall call =
                    JsonSerializer.Deserialize<ScanCall>(payloadJson, Json)
                    ?? throw new PluginRefusedException(NotUnderstood(member));

                IEnumerable<MediaFile> files = await source.ScanAsync(
                    call.Path ?? string.Empty,
                    cancellationToken
                );

                return JsonSerializer.Serialize(files, Json);
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

    private sealed record SearchCall(string? Query, PluginCaller? Caller);

    private sealed record AuthCall(string? Token);

    private sealed record ScanCall(string? Path);

    private static PluginRefusal NoCaller(string member) =>
        new(
            PluginRefusalCodes.HostServicesRemoved,
            "unknown plugin",
            $"The server asked this plugin for {member} without saying who was asking.",
            "That entry point can scope its answer to one user, so a call with no caller cannot be served safely.",
            "This is a server fault rather than a plugin one. Report it with the server log around the call.",
            PluginRefusalSeverity.Blocked
        );

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
