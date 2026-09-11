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
    /// <para>
    /// The two sides fail differently on purpose. A missing
    /// <paramref name="format" /> is a caller's omission, named in words by
    /// the members-present check in front of this one, so it is answered
    /// false. A missing <paramref name="contentType" /> cannot be: it is read
    /// back out of a <c>DerivedAudio</c> row the server wrote itself, so a row
    /// without one is a corrupted register, and reading that back to a plugin
    /// as an ordinary format mismatch would send the owner after the stem
    /// instead of the register. It throws.
    /// </para>
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="contentType" /> is null or blank - a register row the
    /// server cannot have written.
    /// </exception>
    public static bool Matches(string? format, string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            throw new InvalidOperationException("a DerivedAudio row has no content type");
        }

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

    /// <summary>
    /// The casing a stem's format or kind is stored in. Case is not what a
    /// producer is refused over, so one casing has to be chosen when the row
    /// lands: a register holding both "Vocals" and "vocals" holds two stems as
    /// far as every reader of it is concerned, and one of them can never be
    /// found again by a caller that spells it the other way.
    /// <para>
    /// A token that is not there canonicalizes to empty rather than throwing.
    /// The batch-shape checks that address a row by its kind run before the
    /// per-row members check that names a missing one in words, and this is
    /// not the member that should get to report it.
    /// </para>
    /// </summary>
    public static string Canonical(string? value) => value?.ToLowerInvariant() ?? string.Empty;

    private static bool Is(string? value, string expected) =>
        string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
}
