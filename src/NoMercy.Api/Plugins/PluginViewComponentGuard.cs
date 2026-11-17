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

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NoMercy.Design;
using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.Api.Plugins;

/// <summary>
/// Finds a component a plugin may not draw anywhere in its view, before the
/// view reaches a client.
/// <para>
/// It reads the view as JSON, the way a client will. A child can sit in
/// <c>Items</c>, inside <c>props.items</c>, in a named slot or in a dictionary
/// the plugin wrote by hand, and every one of those reaches the client as an
/// object with a <c>component</c> key. Walking the C# objects would have to
/// know every place a child can hide; walking the JSON cannot miss one.
/// </para>
/// </summary>
public static class PluginViewComponentGuard
{
    private const string ComponentKey = "component";

    private static readonly JsonSerializer Serializer = JsonSerializer.Create(
        new JsonSerializerSettings { ReferenceLoopHandling = ReferenceLoopHandling.Ignore }
    );

    /// <summary>
    /// A forbidden component name in <paramref name="view" />, at any
    /// depth, or null when the view names none.
    /// </summary>
    public static string? FindForbidden(PluginView view)
    {
        Stack<JToken> pending = new();
        pending.Push(JToken.FromObject(view, Serializer));

        // A stack rather than recursion: the tree is the plugin's, and its depth
        // is the plugin's choice too.
        while (pending.Count > 0)
        {
            JToken token = pending.Pop();

            if (token is JObject node)
            {
                foreach (JProperty property in node.Properties())
                {
                    if (
                        string.Equals(
                            property.Name,
                            ComponentKey,
                            StringComparison.OrdinalIgnoreCase
                        )
                        && property.Value is JValue { Type: JTokenType.String } value
                        && NmPluginForbiddenComponents.IsForbidden(value.Value<string>())
                    )
                        return value.Value<string>();

                    pending.Push(property.Value);
                }
            }
            else if (token is JArray array)
            {
                foreach (JToken item in array)
                    pending.Push(item);
            }
        }

        return null;
    }
}
