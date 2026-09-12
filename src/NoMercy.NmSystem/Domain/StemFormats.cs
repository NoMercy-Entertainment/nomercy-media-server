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
/// What a stem may be, and what it may be stored as.
/// <para>
/// The producer that writes a stem, the writer that registers one and the
/// encoder that labels an output file all need this vocabulary, and they used
/// to hold a copy each: the encoder's own "opus"/"audio/ogg" literals in its
/// MIME table, the audio tools' own constants, a pairing switch in the
/// register. Two copies of one table drift, and the drift only shows up as a
/// client being handed a file that is not what its register row claims -
/// hours after the sweep that stored it.
/// </para>
/// <para>
/// It lives here rather than next to the derived store because the encoder
/// needs it too, and <c>NoMercy.NmSystem</c> is the assembly all three
/// reach. It sits beside <see cref="MediaTypes" />: both are a vocabulary
/// several assemblies have to agree on, and neither is behaviour.
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
    /// The three ways a side can be missing fail differently, on purpose. A
    /// missing <paramref name="format" /> is a caller's omission, named in
    /// words by the members-present check in front of this one, so it is
    /// answered false. A <em>blank</em> <paramref name="contentType" /> is
    /// neither: it is read back out of a <c>DerivedAudio</c> row the server
    /// wrote itself, and that column does not take null, so blank is a
    /// corrupted register - reading it back to a plugin as an ordinary format
    /// mismatch would send the owner after the stem instead of the register.
    /// It throws. A <em>null</em> <paramref name="contentType" /> is the third
    /// thing and stays quiet: a caller projecting that column gets null when
    /// there is no row at all, which is an ordinary absence for the reader to
    /// refuse in its own words - the register is not corrupt, the entry is
    /// simply gone.
    /// </para>
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="contentType" /> is blank - a register row the server
    /// cannot have written.
    /// </exception>
    public static bool Matches(string? format, string? contentType)
    {
        if (contentType is not null && string.IsNullOrWhiteSpace(contentType))
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
