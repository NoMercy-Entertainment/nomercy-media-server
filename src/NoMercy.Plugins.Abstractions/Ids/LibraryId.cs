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

[JsonConverter(typeof(PluginIdJsonConverter<LibraryId>))]
public readonly record struct LibraryId(Ulid Value)
{
    public static LibraryId Empty { get; } = new(Ulid.Empty);

    public static LibraryId Parse(string value)
    {
        return new LibraryId(Ulid.Parse(value));
    }

    public static bool TryParse(string? value, out LibraryId id)
    {
        if (value is not null && Ulid.TryParse(value, out Ulid parsed))
        {
            id = new LibraryId(parsed);
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
