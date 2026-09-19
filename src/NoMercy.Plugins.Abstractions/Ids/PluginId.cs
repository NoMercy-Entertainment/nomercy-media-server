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

using System.Text.Json.Serialization;

namespace NoMercy.Plugins.Abstractions;

[JsonConverter(typeof(PluginIdJsonConverter<PluginId>))]
public readonly record struct PluginId(Ulid Value)
{
    public static PluginId Empty { get; } = new(Ulid.Empty);

    public static PluginId Parse(string value)
    {
        return new PluginId(Ulid.Parse(value));
    }

    public static bool TryParse(string? value, out PluginId id)
    {
        if (value is not null && Ulid.TryParse(value, out Ulid parsed))
        {
            id = new PluginId(parsed);
            return true;
        }

        id = Empty;
        return false;
    }

    public override string ToString()
    {
        return Value.ToString();
    }
}
