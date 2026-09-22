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
/// The one place a facade call becomes a message, and a refusal becomes the
/// exception a plugin already knows.
/// <para>
/// Every proxy goes through here rather than building its own request, because
/// a facade that spelled its own name differently would be refused by the
/// broker for a reason that named neither the facade nor the plugin.
/// </para>
/// </summary>
public sealed class RemoteCall(Ulid pluginId, IPluginBrokerService broker)
{
    private static readonly JsonSerializerOptions Json = PluginWireJson.Options;

    public async Task<T?> AskAsync<T>(string facade, string member, object? arguments = null)
    {
        string payload = await SendAsync(facade, member, arguments);

        return string.IsNullOrWhiteSpace(payload)
            ? default
            : JsonSerializer.Deserialize<T>(payload, Json);
    }

    public async Task TellAsync(string facade, string member, object? arguments = null) =>
        await SendAsync(facade, member, arguments);

    /// <summary>
    /// The blocking shape, for the two contract members that are not async.
    /// <para>
    /// A facade cannot change a signature the contract already published, so
    /// where the contract is synchronous the wait happens here rather than in
    /// the plugin, which has nowhere to await.
    /// </para>
    /// </summary>
    public T? Ask<T>(string facade, string member, object? arguments = null) =>
        AskAsync<T>(facade, member, arguments).GetAwaiter().GetResult();

    public void Tell(string facade, string member, object? arguments = null) =>
        TellAsync(facade, member, arguments).GetAwaiter().GetResult();

    private async Task<string> SendAsync(string facade, string member, object? arguments)
    {
        PluginCallResponse response = await broker.CallAsync(
            new PluginCallRequest(
                pluginId.ToString(),
                facade,
                member,
                arguments is null ? "null" : JsonSerializer.Serialize(arguments, Json),
                null
            )
        );

        if (response.Ok)
            return response.PayloadJson;

        throw new PluginRefusedException(Translate(response.Refusal, facade, member));
    }

    /// <summary>
    /// A refusal that arrived without a body still has to say something a
    /// plugin author can act on. A silent failure across the boundary reads
    /// as the facade simply not working.
    /// </summary>
    private PluginRefusal Translate(WireRefusal? refusal, string facade, string member)
    {
        if (refusal is null)
            return new PluginRefusal(
                PluginRefusalCodes.HostServicesRemoved,
                pluginId.ToString(),
                $"The plugin called {facade}.{member}.",
                "The server refused the call and sent no reason with it.",
                "Report this with the server log around the call. Docs: /nomercy-plugins/handbook/runtime-and-isolation",
                PluginRefusalSeverity.Blocked
            );

        return new PluginRefusal(
            refusal.Code,
            refusal.Plugin,
            refusal.What,
            refusal.Why,
            refusal.Fix,
            Enum.TryParse(refusal.Severity, ignoreCase: true, out PluginRefusalSeverity severity)
                ? severity
                : PluginRefusalSeverity.Blocked
        );
    }
}
