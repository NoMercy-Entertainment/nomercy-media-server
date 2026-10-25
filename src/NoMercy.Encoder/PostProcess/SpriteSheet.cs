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

using System.Text.RegularExpressions;

namespace NoMercy.Encoder.PostProcess;

/// <summary>
/// The scrub-preview sprite sheet, named for what it contains:
/// <c>thumbs_{width}x{height}.webp</c> beside its <c>thumbs_{width}x{height}.vtt</c>.
///
/// <para>Because the tile size is in the filename, a folder on disk states the
/// resolution its preview was built at — no database column, no sidecar, no
/// probing. That is what lets a library scan tell an old sheet from a current
/// one and top up only the ones that fall short.</para>
/// </summary>
public static partial class SpriteSheet
{
    /// <summary>
    /// The narrowest tile worth shipping, and the default every preset inherits.
    ///
    /// <para>A TV draws a scrub tile around 560 physical pixels wide, so 160 was
    /// being blown up three and a half times — the pixellation is not subtle at
    /// couch distance. 320 is deliberately not higher: the sheet is one image
    /// laid out on a square grid, clients decode it whole, and every doubling of
    /// the tile doubles both sheet dimensions. Past this point a typical title
    /// crosses the decoder's own size ceiling and gets halved on the way in,
    /// which hands back exactly what the larger tile bought.</para>
    /// </summary>
    public const int MinimumWidth = 320;

    [GeneratedRegex(@"^thumbs_(?<width>\d+)x(?<height>\d+)\.webp$", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SpriteFileNameRegex();

    [GeneratedRegex(@"^sprite\.webp$", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex LegacySpriteFileNameRegex();

    /// <summary>
    /// A sheet from before the tile size went into the filename, which is always
    /// narrower than the floor and never carries a cue file the clients can read.
    /// </summary>
    public static bool IsLegacySheet(string fileName) =>
        LegacySpriteFileNameRegex().IsMatch(fileName);

    /// <summary>
    /// The tile width a sprite sheet was rendered at, read from its filename, or
    /// null when the name is not a sprite sheet.
    /// </summary>
    public static int? ReadTileWidth(string fileName)
    {
        Match match = SpriteFileNameRegex().Match(fileName);
        return match.Success ? int.Parse(match.Groups["width"].Value) : null;
    }

    /// <summary>
    /// The sprite sheets in <paramref name="fileNames"/> whose tiles are narrower
    /// than <paramref name="minimumWidth"/>.
    ///
    /// <para>A folder that already carries a wide-enough sheet yields nothing,
    /// even if a narrow one sits beside it: an older sheet left behind by a
    /// previous render is dead weight, not a job to run.</para>
    /// </summary>
    public static IReadOnlyList<string> SelectUndersized(
        IEnumerable<string> fileNames,
        int minimumWidth = MinimumWidth
    )
    {
        List<string> undersized = [];
        bool hasAdequate = false;

        foreach (string fileName in fileNames)
        {
            // A legacy sheet states no tile size, so nothing here could read one
            // and every one of them was skipped — the folder then looked like it
            // held no sheet at all and no rebuild was ever queued. Measured on the
            // dev library 2026-08-18: 281 of 840 playables carried `sprite.webp`,
            // no cue file, and no scrub preview on any client.
            if (IsLegacySheet(fileName))
            {
                undersized.Add(fileName);
                continue;
            }

            int? width = ReadTileWidth(fileName);
            if (width is null)
                continue;

            if (width >= minimumWidth)
                hasAdequate = true;
            else
                undersized.Add(fileName);
        }

        return hasAdequate ? [] : undersized;
    }
}
