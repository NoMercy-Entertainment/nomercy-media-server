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

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace NoMercy.OpticalMedia.Metadata;

/// <summary>
/// Computes the MusicBrainz Disc ID from a CD Table of Contents.
///
/// Algorithm (matching libdiscid / Picard):
///   1. Build an 804-hex-char input string:
///      - 2-hex: first track number (zero-padded, upper-case)
///      - 2-hex: last track number (zero-padded, upper-case)
///      - 8-hex: lead-out offset
///      - 99 x 8-hex: one slot per track number 1..99 holding that track's
///        offset; slots outside FirstTrack..LastTrack are 00000000
///   2. SHA1 of the ASCII input string.
///   3. Base64-encode the 20 raw SHA1 bytes.
///   4. Apply MusicBrainz alphabet substitutions:
///      <c>+</c> -> <c>.</c>,  <c>/</c> -> <c>_</c>,  <c>=</c> -> <c>-</c>
///
/// Offsets are the absolute frame offsets the spec hashes directly (track 1 is
/// normally 150). Converting raw LBA to absolute (the 150-frame pre-gap) is the
/// TOC reader's job, not this method's — see <see cref="ITocReader"/>.
/// </summary>
public static class MusicBrainzDiscId
{
    private const int TocSlots = 99;

    /// <summary>
    /// Computes the MusicBrainz Disc ID for the given TOC.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown when the TOC is structurally invalid (e.g. track offsets
    /// length does not match <c>LastTrack - FirstTrack + 1</c>).
    /// </exception>
    public static string Compute(DiscToc toc)
    {
        int trackCount = toc.LastTrack - toc.FirstTrack + 1;
        if (toc.TrackOffsetsSectors.Length != trackCount)
        {
            throw new ArgumentException(
                $"TrackOffsetsSectors length ({toc.TrackOffsetsSectors.Length}) "
                    + $"does not match LastTrack - FirstTrack + 1 ({trackCount}).",
                nameof(toc)
            );
        }

        StringBuilder sb = new(capacity: 4 + 8 + TocSlots * 8);

        sb.Append(toc.FirstTrack.ToString("X2", CultureInfo.InvariantCulture));
        sb.Append(toc.LastTrack.ToString("X2", CultureInfo.InvariantCulture));
        sb.Append(toc.LeadOutOffsetSectors.ToString("X8", CultureInfo.InvariantCulture));

        for (int trackNumber = 1; trackNumber <= TocSlots; trackNumber++)
        {
            bool onDisc = trackNumber >= toc.FirstTrack && trackNumber <= toc.LastTrack;
            int offset = onDisc ? toc.TrackOffsetsSectors[trackNumber - toc.FirstTrack] : 0;
            sb.Append(offset.ToString("X8", CultureInfo.InvariantCulture));
        }

        byte[] inputBytes = Encoding.ASCII.GetBytes(sb.ToString());
        byte[] sha1 = SHA1.HashData(inputBytes);

        string base64 = Convert.ToBase64String(sha1);
        return base64.Replace('+', '.').Replace('/', '_').Replace('=', '-');
    }
}
