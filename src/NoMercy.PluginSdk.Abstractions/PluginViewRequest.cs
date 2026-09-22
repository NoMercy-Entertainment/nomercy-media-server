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
using System.Text.Json.Serialization;

namespace NoMercy.PluginSdk.Abstractions;

/// <summary>Which of a plugin's routes a client is asking to render.</summary>
public class PluginViewRequest
{
    [JsonPropertyName("route")]
    public required string Route { get; init; }

    [JsonPropertyName("query")]
    public Dictionary<string, string> Query { get; init; } = new();

    /// <summary>
    /// Who is asking, with the role and access the host resolved. A view is
    /// served on the caller's behalf, not the server's.
    /// </summary>
    [JsonPropertyName("caller")]
    public required PluginCaller Caller { get; init; }

    /// <summary>
    /// The caller's id, so a plugin that only wants that keeps reading it.
    /// <para>
    /// A string, because that is what every plugin built against 11.0 reads.
    /// Typed, it kept its name and changed its signature, which compiles
    /// everywhere and then throws MissingMethodException on the first view a
    /// plugin serves. <see cref="CallerId" /> is the same id, typed.
    /// </para>
    /// </summary>
    [JsonPropertyName("userId")]
    public string UserId => Caller.Id.ToString();

    /// <summary>The same id, as the host holds it.</summary>
    [JsonIgnore]
    public UserId CallerId => Caller.Id;

    /// <summary>
    /// Which kind of screen is asking, from <see cref="PluginSurface" />.
    ///
    /// A television is not a narrow desktop. Some views differ by a hidden
    /// column, which a component handles itself through its box, and some are a
    /// different page entirely — a grid of posters on a TV where the desktop
    /// shows a table. A plugin that cannot tell them apart has to pick one and
    /// be wrong on the other two.
    ///
    /// Defaults to the roomiest surface, so a caller that says nothing gets the
    /// view with the most in it rather than the most stripped down.
    /// </summary>
    [JsonPropertyName("surface")]
    public string Surface { get; init; } = PluginSurface.Web;

    /// <summary>
    /// What the caller typed, keyed by the field the plugin named. Empty for a
    /// plain page view, so a plugin written before forms existed behaves
    /// exactly as it did.
    /// <para>
    /// Untrusted: these are values a person entered, reaching plugin code.
    /// </para>
    /// </summary>
    [JsonPropertyName("values")]
    public IReadOnlyDictionary<string, object?> Values { get; init; } =
        new Dictionary<string, object?>();

    /// <summary>
    /// Which button was pressed, when the view offered more than one. Null for
    /// a page that was simply opened.
    /// </summary>
    [JsonPropertyName("action")]
    public string? Action { get; init; }

    /// <summary>
    /// One value, or null when the caller sent nothing for that field. Typed
    /// rather than cast at the call site: a number arrives from JSON as a
    /// JsonElement, and every plugin unwrapping that by hand is every plugin
    /// getting it wrong in its own way.
    /// </summary>
    public T? Value<T>(string field)
    {
        if (!Values.TryGetValue(field, out object? raw) || raw is null)
            return default;

        if (raw is JsonElement element)
        {
            try
            {
                return element.Deserialize<T>();
            }
            catch (JsonException)
            {
                return default;
            }
        }

        // Hands back what it was given when the shape already matches, which
        // is why there is no fast path above it: one would be a second way of
        // saying the same thing, and neither could then be proven.
        try
        {
            return (T)Convert.ChangeType(raw, typeof(T));
        }
        catch (Exception exception)
            when (exception is InvalidCastException or FormatException or OverflowException)
        {
            return default;
        }
    }
}
