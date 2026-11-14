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

/// <summary>
/// Reads a catalog version's capability list without ever failing the index.
/// <para>
/// A capability is a name. An element that is not a string is kept as its raw
/// JSON text, so the signature check reports the mismatch for that one version
/// instead of the parser dropping every plugin the repository lists.
/// </para>
/// </summary>
public sealed class PluginCapabilityListJsonConverter : JsonConverter<List<string>>
{
    public override List<string> Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    )
    {
        List<string> capabilities = [];

        if (reader.TokenType != JsonTokenType.StartArray)
        {
            reader.Skip();
            return capabilities;
        }

        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType == JsonTokenType.String)
            {
                capabilities.Add(reader.GetString() ?? string.Empty);
                continue;
            }

            using JsonDocument element = JsonDocument.ParseValue(ref reader);
            capabilities.Add(element.RootElement.GetRawText());
        }

        return capabilities;
    }

    public override void Write(
        Utf8JsonWriter writer,
        List<string> value,
        JsonSerializerOptions options
    )
    {
        writer.WriteStartArray();
        foreach (string capability in value)
            writer.WriteStringValue(capability);
        writer.WriteEndArray();
    }
}
