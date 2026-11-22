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
using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.Api.Plugins;

/// <summary>
/// Works on the actions inside a view, read as JSON the way a client will: an
/// action can sit in a component, a table cell or a dictionary the plugin wrote
/// by hand, and every one reaches the client as an object with a type and a
/// payload.
/// </summary>
public static class PluginViewActionGuard
{
    /// <summary>
    /// The view as it goes out to a caller the owner-only actions are not for:
    /// every action whose access is owner is removed.
    /// </summary>
    public static JToken WithoutOwnerActions(PluginView view, JsonSerializerSettings settings)
    {
        Strip(view, settings, out JToken stripped);
        return stripped;
    }

    /// <summary>Whether the view held an owner-only action; the view without them comes back either way.</summary>
    public static bool Strip(PluginView view, JsonSerializerSettings settings, out JToken stripped)
    {
        stripped = JToken.FromObject(view, JsonSerializer.Create(settings));
        List<JObject> owned = Actions(stripped).Where(IsOwnerOnly).ToList();

        foreach (JObject action in owned)
        {
            if (action.Parent is JProperty property)
                property.Remove();
            else
                action.Remove();
        }

        return owned.Count > 0;
    }

    /// <summary>The view as the plugin built it, owner-only actions still in place.</summary>
    public static JToken Read(PluginView view, JsonSerializerSettings settings) =>
        JToken.FromObject(view, JsonSerializer.Create(settings));

    /// <summary>Whether every action of this type in the view is shared; a type an owner action also uses is not.</summary>
    public static bool OffersOnlySharedAction(JToken view, string type)
    {
        List<JObject> ofType = Actions(view).Where(action => IsType(action, type)).ToList();

        return ofType.Count > 0 && !ofType.Any(IsOwnerOnly);
    }

    /// <summary>Whether a view holds an owner-only action of this type.</summary>
    public static bool HoldsOwnerAction(JToken view, string type) =>
        Actions(view).Any(action => IsOwnerOnly(action) && IsType(action, type));

    private static bool IsType(JObject action, string type) =>
        string.Equals(Text(action, "type"), type, StringComparison.Ordinal);

    private static IEnumerable<JObject> Actions(JToken root) =>
        root.SelectTokens("$..*")
            .OfType<JObject>()
            .Where(node => Text(node, "type") is not null && HasPayloadOrAccess(node));

    private static bool HasPayloadOrAccess(JObject node) =>
        node.Properties().Any(Named("payload")) || node.Properties().Any(Named("access"));

    private static bool IsOwnerOnly(JObject action)
    {
        JToken? access = action.Properties().FirstOrDefault(Named("access"))?.Value;

        return access is { Type: JTokenType.String }
            ? string.Equals(access.Value<string>(), "owner", StringComparison.OrdinalIgnoreCase)
            : access is { Type: JTokenType.Integer }
                && access.Value<int>() == (int)PluginRouteAccess.Owner;
    }

    private static Func<JProperty, bool> Named(string name) =>
        property => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase);

    private static string? Text(JObject node, string name) =>
        node.Properties().FirstOrDefault(Named(name))?.Value is { Type: JTokenType.String } value
            ? value.Value<string>()
            : null;
}
