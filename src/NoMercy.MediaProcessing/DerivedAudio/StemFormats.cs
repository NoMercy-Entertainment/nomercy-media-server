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

namespace NoMercy.MediaProcessing.DerivedAudio;

/// <summary>
/// What a stem may be, and what it may be stored as.
/// <para>
/// The producer that writes a stem and the writer that registers one both
/// need this vocabulary, and they used to hold a copy each: the encoder's own
/// "opus"/"audio/ogg" constants on one side, a pairing switch on the other.
/// Two copies of one table drift, and the drift only shows up as a client
/// being handed a file that is not what its register row claims - hours after
/// the sweep that stored it.
/// </para>
/// </summary>
public static class StemFormats
{
    /// <summary>The format the stem splitter writes today.</summary>
    public const string Opus = "opus";

    /// <summary>The lossless format a producer may write instead.</summary>
    public const string Flac = "flac";

    /// <summary>Opus in an Ogg container - what <c>PutAsync</c> is handed.</summary>
    public const string OggContentType = "audio/ogg";

    /// <summary>The other MIME a third-party producer may put Opus under.</summary>
    public const string OpusContentType = "audio/opus";

    /// <summary>FLAC.</summary>
    public const string FlacContentType = "audio/flac";

    /// <summary>
    /// True only for a pairing the derived store can actually hold. Anything
    /// else is a mismatch rather than an unknown: a format that cannot come
    /// out of that container is not a register row worth keeping.
    /// <para>
    /// Case is not what a caller is refused over - a producer writing "OPUS"
    /// means the same format - but the pair itself is.
    /// </para>
    /// </summary>
    public static bool Matches(string format, string contentType)
    {
        if (Is(format, Opus))
        {
            return Is(contentType, OggContentType) || Is(contentType, OpusContentType);
        }

        if (Is(format, Flac))
        {
            return Is(contentType, FlacContentType);
        }

        return false;
    }

    private static bool Is(string value, string expected) =>
        string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
}
