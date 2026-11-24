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

using System.Collections;
using System.Globalization;
using System.Reflection;
using Newtonsoft.Json.Linq;

namespace NoMercy.Api.Hubs.Filters;

/// <summary>
/// A short log summary of hub arguments. Only values under an allow-listed name are shown,
/// and a string only when it is a short identifier, so a token or a name cannot reach a log.
/// </summary>
internal static class HubArgumentSummary
{
    private static readonly HashSet<string> AllowedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "id",
        "deviceid",
        "itemid",
        "listid",
        "trackid",
        "newtrackid",
        "videoid",
        "playlistid",
        "type",
        "listtype",
        "mediatype",
        "mediaid",
        "playlisttype",
        "command",
        "time",
        "seconds",
        "position",
        "positionms",
        "capturedatms",
        "duration",
        "fadedurationms",
        "volume",
        "muted",
        "season",
        "episode",
        "index",
        "item",
        "audiotrack",
        "subtitletrack",
        "resumeat",
        "from",
        "to",
        "target",
    };

    private static readonly string[] SensitiveFragments =
    [
        "token",
        "auth",
        "password",
        "secret",
        "bearer",
        "cookie",
        "apikey",
        "email",
    ];

    public static string Describe(MethodInfo method, IReadOnlyList<object?> arguments)
    {
        if (arguments.Count == 0)
            return string.Empty;

        ParameterInfo[] parameters = method.GetParameters();
        List<string> parts = [];
        for (int index = 0; index < arguments.Count; index++)
        {
            string name = index < parameters.Length ? parameters[index].Name ?? "arg" : "arg";
            parts.Add(DescribeValue(name, arguments[index]));
        }

        return string.Join(", ", parts);
    }

    private static string DescribeValue(string name, object? value)
    {
        if (value is JValue jValue)
            value = jValue.Value;

        if (value is null)
            return $"{name}=null";

        string key = name.Replace("_", string.Empty, StringComparison.Ordinal);
        if (IsSensitive(key))
            return $"{name}=<hidden>";

        if (value is JObject jObject)
            return $"{name}{{{DescribeProperties(jObject.Properties().Select(p => (p.Name, (object?)p.Value)))}}}";

        if (value is JArray jArray)
            return $"{name}[{jArray.Count}]";

        if (value is string text)
            return
                AllowedNames.Contains(key) && SessionLogText.SafeIdentifierOrNull(text) is { } safe
                ? $"{name}={safe}"
                : $"{name}=<string>";

        if (value is IEnumerable and not string)
            return $"{name}[{((IEnumerable)value).Cast<object?>().Count()}]";

        if (value is bool or Guid or Ulid || value.GetType().IsPrimitive || value.GetType().IsEnum)
            return $"{name}={Format(value)}";

        if (
            value.GetType().IsValueType
            || value.GetType().Namespace?.StartsWith("System", StringComparison.Ordinal) == true
        )
            return $"{name}=<{value.GetType().Name}>";

        return $"{name}{{{DescribeProperties(value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.GetIndexParameters().Length == 0).Select(p => (p.Name, p.GetValue(value))))}}}";
    }

    private static string DescribeProperties(IEnumerable<(string Name, object? Value)> properties)
    {
        List<string> shown = [];
        int hidden = 0;
        foreach ((string name, object? value) in properties)
        {
            string key = name.Replace("_", string.Empty, StringComparison.Ordinal);
            object? plain = value is JValue jValue ? jValue.Value : value;
            if (
                IsSensitive(key)
                || !AllowedNames.Contains(key)
                || plain is null
                || (
                    plain is not (string or bool or Guid or Ulid)
                    && !plain.GetType().IsPrimitive
                    && !plain.GetType().IsEnum
                )
                || (plain is string text && SessionLogText.SafeIdentifierOrNull(text) is null)
            )
            {
                hidden++;
                continue;
            }

            shown.Add($"{name}={Format(plain)}");
        }

        if (hidden > 0)
            shown.Add($"+{hidden} hidden");

        return string.Join(", ", shown);
    }

    private static bool IsSensitive(string key) =>
        SensitiveFragments.Any(fragment =>
            key.Contains(fragment, StringComparison.OrdinalIgnoreCase)
        );

    private static string Format(object value) =>
        value is IFormattable formattable
            ? formattable.ToString(null, CultureInfo.InvariantCulture)
            : value.ToString() ?? string.Empty;
}
