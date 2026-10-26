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

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

namespace NoMercy.Data.Visualizers;

/// <summary>
/// Canonical JSON of a payload: keys sorted ordinally at every depth, no
/// whitespace, UTF-8. Text and numbers follow JavaScript's JSON.stringify so the
/// content repo build and this server hash the same bytes.
/// </summary>
public static class VisualizerCanonicalJson
{
    public static string Serialize(JToken token)
    {
        StringBuilder builder = new();
        Write(builder, token);
        return builder.ToString();
    }

    public static string Hash(string canonicalText)
    {
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalText)));
    }

    private static void Write(StringBuilder builder, JToken token)
    {
        switch (token.Type)
        {
            case JTokenType.Object:
                WriteObject(builder, (JObject)token);
                break;
            case JTokenType.Array:
                builder.Append('[');
                bool firstItem = true;
                foreach (JToken item in (JArray)token)
                {
                    if (!firstItem)
                        builder.Append(',');
                    firstItem = false;
                    Write(builder, item);
                }
                builder.Append(']');
                break;
            case JTokenType.String:
                WriteString(builder, token.Value<string>() ?? "");
                break;
            case JTokenType.Integer:
            case JTokenType.Float:
                double number = ((JValue)token).Value is System.Numerics.BigInteger big
                    ? double.Parse(
                        big.ToString(CultureInfo.InvariantCulture),
                        CultureInfo.InvariantCulture
                    )
                    : token.Value<double>();
                builder.Append(FormatNumber(number));
                break;
            case JTokenType.Boolean:
                builder.Append(token.Value<bool>() ? "true" : "false");
                break;
            default:
                builder.Append("null");
                break;
        }
    }

    private static void WriteObject(StringBuilder builder, JObject obj)
    {
        builder.Append('{');
        bool first = true;
        foreach (
            JProperty property in obj.Properties().OrderBy(p => p.Name, StringComparer.Ordinal)
        )
        {
            if (!first)
                builder.Append(',');
            first = false;
            WriteString(builder, property.Name);
            builder.Append(':');
            Write(builder, property.Value);
        }
        builder.Append('}');
    }

    private static void WriteString(StringBuilder builder, string value)
    {
        builder.Append('"');
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            switch (c)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (c < 0x20 || IsLoneSurrogate(value, i))
                        builder
                            .Append("\\u")
                            .Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        builder.Append(c);
                    break;
            }
        }
        builder.Append('"');
    }

    private static bool IsLoneSurrogate(string value, int index)
    {
        char c = value[index];
        if (char.IsHighSurrogate(c))
            return index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1]);
        if (char.IsLowSurrogate(c))
            return index == 0 || !char.IsHighSurrogate(value[index - 1]);
        return false;
    }

    /// <summary>JavaScript Number::toString for a finite double.</summary>
    public static string FormatNumber(double value)
    {
        if (value == 0)
            return "0";

        string sign = value < 0 ? "-" : "";
        string shortest = Math.Abs(value).ToString("R", CultureInfo.InvariantCulture);

        int exponent = 0;
        int exponentAt = shortest.IndexOf('E');
        if (exponentAt >= 0)
        {
            exponent = int.Parse(shortest[(exponentAt + 1)..], CultureInfo.InvariantCulture);
            shortest = shortest[..exponentAt];
        }

        int dot = shortest.IndexOf('.');
        string integerPart = dot >= 0 ? shortest[..dot] : shortest;
        string fractionPart = dot >= 0 ? shortest[(dot + 1)..] : "";

        string digits = integerPart + fractionPart;
        int pointAt = integerPart.Length + exponent;

        int leading = 0;
        while (leading < digits.Length - 1 && digits[leading] == '0')
            leading++;
        digits = digits[leading..];
        pointAt -= leading;
        digits = digits.TrimEnd('0');

        int count = digits.Length;
        if (count <= pointAt && pointAt <= 21)
            return sign + digits + new string('0', pointAt - count);
        if (0 < pointAt && pointAt <= 21)
            return sign + digits[..pointAt] + "." + digits[pointAt..];
        if (-6 < pointAt && pointAt <= 0)
            return sign + "0." + new string('0', -pointAt) + digits;

        int power = pointAt - 1;
        string powerText =
            (power < 0 ? "-" : "+") + Math.Abs(power).ToString(CultureInfo.InvariantCulture);
        string mantissa = count == 1 ? digits : digits[0] + "." + digits[1..];
        return sign + mantissa + "e" + powerText;
    }
}
