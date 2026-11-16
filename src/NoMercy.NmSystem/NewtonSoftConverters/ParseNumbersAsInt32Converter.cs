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

namespace NoMercy.NmSystem.NewtonSoftConverters;

public class ParseNumbersAsInt32Converter : JsonConverter
{
    public override bool CanConvert(Type objectType)
    {
        return objectType == typeof(long) || objectType == typeof(object);
    }

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
    {
        if (value is int intValue)
        {
            writer.WriteValue(intValue);
        }
        // Fallback to default serialization for other types
        // serializer.Serialize(writer, value);
    }

    public override object? ReadJson(
        JsonReader reader,
        Type objectType,
        object? existingValue,
        JsonSerializer serializer
    )
    {
        // An object or array has no reader.Value; returning without reading it leaves the
        // reader inside it, which fails every object-typed slot that receives one.
        if (reader.TokenType is JsonToken.StartObject or JsonToken.StartArray)
            return JToken.Load(reader);

        return reader.Value is long ? Convert.ToInt64(reader.Value ?? 0) : reader.Value;
    }
}
