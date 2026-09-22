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
/// Reads a plugin id that was written either as a Ulid or, in the manifests
/// published before this platform settled on Ulid, as a GUID.
/// <para>
/// A plugin already in the field carries whatever its author wrote, and a
/// server update does not get to stop loading it. The two forms are the same
/// sixteen bytes, so a GUID id resolves to one fixed Ulid: the plugin keeps its
/// identity, its stored consent and its grants across the change. Everything
/// this server writes back out is the Ulid form.
/// </para>
/// </summary>
public class PluginIdJsonConverter : JsonConverter<Ulid>
{
    public override Ulid Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    )
    {
        if (reader.TokenType == JsonTokenType.Null)
            return Ulid.Empty;

        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException($"Expected a string plugin id, got {reader.TokenType}.");

        string value = reader.GetString() ?? string.Empty;

        if (value.Length == 0)
            return Ulid.Empty;

        if (Ulid.TryParse(value, out Ulid ulid))
            return ulid;

        if (Guid.TryParse(value, out Guid guid))
            return new(guid);

        throw new JsonException($"Plugin id is neither a Ulid nor a GUID: '{value}'.");
    }

    public override void Write(Utf8JsonWriter writer, Ulid value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString());
    }
}
