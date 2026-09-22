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

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NoMercy.PluginSdk.Abstractions;

public sealed class PluginIdJsonConverter<T> : JsonConverter<T>
    where T : struct
{
    public override T Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    )
    {
        string text = reader.GetString() ?? string.Empty;

        try
        {
            object? parsed = typeof(T).GetMethod("Parse", [typeof(string)])!.Invoke(null, [text]);
            return (T)parsed!;
        }
        catch (TargetInvocationException exception)
        {
            throw new JsonException(
                $"'{text}' is not a valid {typeToConvert.Name}.",
                exception.InnerException
            );
        }
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString());
    }
}
