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
using NoMercy.PluginSdk.Ipc;

namespace NoMercy.Api.Plugins;

/// <summary>
/// What of an inbound request is allowed to reach a plugin's own process.
/// <para>
/// The server has already authenticated the caller by the time a request gets
/// here, and the plugin never needs to do it again. So the credential does not
/// travel: a plugin process holding the owner's bearer token could call the
/// server's whole API as them, which is more than any capability grants and
/// more than the owner agreed to when they installed it.
/// </para>
/// <para>
/// Who is asking travels instead, as a header the server writes, so the plugin
/// can tell one user from another without ever being able to act as one.
/// </para>
/// </summary>
public static class PluginReverseProxyRequest
{
    public const string CallerHeader = "x-nomercy-plugin-caller";

    /// <summary>
    /// Headers that must never cross, whatever the request carried.
    /// <para>
    /// A deny list rather than an allow list would be the wrong way round for
    /// a credential — one header nobody thought of and the token is through —
    /// so anything not recognised here is dropped as well.
    /// </para>
    /// </summary>
    private static readonly HashSet<string> NeverForwarded = new(StringComparer.OrdinalIgnoreCase)
    {
        "authorization",
        "cookie",
        "set-cookie",
        "proxy-authorization",
        "x-api-key",
        CallerHeader,
    };

    /// <summary>
    /// Headers a plugin can reasonably need and that carry no credential.
    /// Everything else is dropped rather than forwarded, because a header the
    /// server does not recognise is one nobody has decided is safe.
    /// </summary>
    private static readonly HashSet<string> Forwarded = new(StringComparer.OrdinalIgnoreCase)
    {
        "accept",
        "accept-language",
        "content-type",
        "content-length",
        "range",
        "if-none-match",
        "if-modified-since",
        "user-agent",
    };

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The headers the child process receives: the safe ones from the caller,
    /// plus who is asking.
    /// </summary>
    /// <param name="allowed">
    /// Which headers may cross. Defaults to the set above; a test passes its
    /// own to prove the deny list still catches a credential somebody added to
    /// the allow list by mistake.
    /// </param>
    public static Dictionary<string, string> HeadersFor(
        IReadOnlyDictionary<string, string> inbound,
        WireCaller caller,
        IReadOnlySet<string>? allowed = null
    )
    {
        IReadOnlySet<string> crossing = allowed ?? Forwarded;

        Dictionary<string, string> forwarded = new(StringComparer.OrdinalIgnoreCase);

        foreach ((string name, string value) in inbound)
        {
            if (NeverForwarded.Contains(name) || !crossing.Contains(name))
                continue;

            forwarded[name] = value;
        }

        // Written by the server, after the inbound headers, so a request that
        // arrived carrying this header cannot pretend to be somebody else.
        forwarded[CallerHeader] = JsonSerializer.Serialize(caller, Json);

        return forwarded;
    }

    public static WireCaller? ReadCaller(string? headerValue)
    {
        if (string.IsNullOrWhiteSpace(headerValue))
            return null;

        try
        {
            return JsonSerializer.Deserialize<WireCaller>(headerValue, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
