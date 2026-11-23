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

namespace NoMercy.NmSystem.Domain;

/// <summary>
/// The audio formats a stem is written in and the content types they are
/// served under. One table, so the encoder that labels an output file and
/// whoever reads that label agree on it.
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
}
