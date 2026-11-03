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

namespace NoMercy.Database;

public class ColorPalette
{
    [JsonProperty("poster", NullValueHandling = NullValueHandling.Ignore)]
    public PaletteColors? Poster { get; set; }

    [JsonProperty("backdrop", NullValueHandling = NullValueHandling.Ignore)]
    public PaletteColors? Backdrop { get; set; }

    [JsonProperty("still", NullValueHandling = NullValueHandling.Ignore)]
    public PaletteColors? Still { get; set; }

    [JsonProperty("profile", NullValueHandling = NullValueHandling.Ignore)]
    public PaletteColors? Profile { get; set; }

    [JsonProperty("image", NullValueHandling = NullValueHandling.Ignore)]
    public PaletteColors? Image { get; set; }

    [JsonProperty("cover", NullValueHandling = NullValueHandling.Ignore)]
    public PaletteColors? Cover { get; set; }

    /// <summary>
    /// The marker <c>PaletteResult.NoImage</c> persists for an entity that has no
    /// image to sample. Terminal: there is nothing to paint.
    /// </summary>
    public const string NoImageJson = "{}";

    /// <summary>
    /// A colourless entry serialises as an empty object nested in the palette,
    /// e.g. {"poster":{}}. A real palette never contains one, and the
    /// <see cref="NoImageJson"/> marker is the whole string, not nested. This is
    /// the shape the SQL-side needs-palette filters match with Contains.
    /// </summary>
    public const string ColourlessEntryJson = "{}";

    [JsonIgnore]
    public bool HasColors =>
        Poster is { HasColors: true }
        || Backdrop is { HasColors: true }
        || Still is { HasColors: true }
        || Profile is { HasColors: true }
        || Image is { HasColors: true }
        || Cover is { HasColors: true };

    /// <summary>
    /// True when a stored palette string needs no further work: either the
    /// <see cref="NoImageJson"/> marker or a palette with at least one colour.
    /// Blank, malformed and colourless values ({"poster":{}} from a failed
    /// download) are unfinished, so the next palette pass paints them.
    /// </summary>
    public static bool IsFinished(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return false;
        if (json == NoImageJson)
            return true;
        return FromJsonOrNull(json) is { HasColors: true };
    }

    /// <summary>
    /// Forgiving deserializer for the persisted color-palette JSON. Returns
    /// null for empty/missing/malformed strings so a corrupted DB row never
    /// takes a card render down. Use this everywhere instead of calling
    /// <see cref="JsonConvert.DeserializeObject{T}(string)"/> directly.
    /// </summary>
    public static ColorPalette? FromJsonOrNull(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return null;
        try
        {
            return JsonConvert.DeserializeObject<ColorPalette>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
