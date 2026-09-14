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

namespace NoMercy.MediaProcessing.Images;

/// <summary>Decodes an uploaded <c>data:image/...;base64,...</c> payload.</summary>
public static partial class ImageDataUri
{
    public const string NotADataUri = "Cover must be a data:image/...;base64,... payload";
    public const string NotBase64 = "Cover payload is not valid base64";

    [GeneratedRegex("data:image/(?<type>.+?),(?<data>.+)")]
    private static partial Regex DataUriRegex();

    /// <summary>
    /// The image bytes, or null with <paramref name="error"/> saying why the payload
    /// was rejected.
    /// </summary>
    public static byte[]? Decode(string payload, out string? error)
    {
        Match match = DataUriRegex().Match(payload);
        if (!match.Success)
        {
            error = NotADataUri;
            return null;
        }

        try
        {
            error = null;
            return Convert.FromBase64String(match.Groups["data"].Value);
        }
        catch (FormatException)
        {
            error = NotBase64;
            return null;
        }
    }
}
