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

namespace NoMercy.Encoder.ContentAnalysis.Fingerprinting;

/// <summary>
/// Turns the chromaprint muxer's <c>-fp_format base64</c> output back into the
/// raw 32-bit hashes the intro detector compares.
/// <para>
/// The base64 form is chromaprint's compressed fingerprint, URL-safe base64
/// without padding. The compressed layout (libchromaprint
/// <c>fingerprint_compressor.cpp</c>):
/// <list type="number">
/// <item>1 byte algorithm, then the hash count as a 24-bit big-endian number.</item>
/// <item>Each hash is XOR-ed with the previous one; the set bits of the result
/// are written as distances between bit positions, one 3-bit value each, with
/// a 0 closing the hash. A distance of 7 or more is written as 7.</item>
/// <item>For every 7, a 5-bit value holding the rest of the distance follows
/// in a second packed area.</item>
/// </list>
/// Both areas pack their values least-significant bit first.
/// </para>
/// </summary>
internal static class ChromaprintFingerprintDecoder
{
    private const int HeaderSize = 4;
    private const int NormalBitWidth = 3;
    private const int ExceptionBitWidth = 5;
    private const int MaxNormalValue = 7;

    /// <summary>
    /// Decodes the muxer's base64 output. Returns an empty array for empty,
    /// malformed or truncated input — a fingerprint that cannot be read is
    /// treated like one that has no frames.
    /// </summary>
    public static uint[] DecodeBase64(string encoded)
    {
        byte[]? compressed = FromUrlSafeBase64(encoded.Trim());
        return compressed is null ? [] : Decompress(compressed);
    }

    internal static uint[] Decompress(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize)
            return [];

        int count = (data[1] << 16) | (data[2] << 8) | data[3];
        if (count == 0)
            return [];

        ReadOnlySpan<byte> normalArea = data[HeaderSize..];
        int maxNormalValues = normalArea.Length * 8 / NormalBitWidth;

        List<int> distances = [];
        int hashesClosed = 0;
        int exceptionCount = 0;

        for (int i = 0; i < maxNormalValues && hashesClosed < count; i++)
        {
            int value = ReadPacked(normalArea, i, NormalBitWidth);
            distances.Add(value);

            if (value == 0)
                hashesClosed++;
            else if (value == MaxNormalValue)
                exceptionCount++;
        }

        if (hashesClosed != count)
            return [];

        int normalAreaBytes = (distances.Count * NormalBitWidth + 7) / 8;
        ReadOnlySpan<byte> exceptionArea = normalArea[normalAreaBytes..];
        if (exceptionArea.Length * 8 < exceptionCount * ExceptionBitWidth)
            return [];

        uint[] hashes = new uint[count];
        int hashIndex = 0;
        int exceptionIndex = 0;
        int bitPosition = 0;
        uint delta = 0;

        foreach (int distance in distances)
        {
            if (distance == 0)
            {
                hashes[hashIndex] = hashIndex > 0 ? delta ^ hashes[hashIndex - 1] : delta;
                hashIndex++;
                bitPosition = 0;
                delta = 0;
                continue;
            }

            int fullDistance =
                distance == MaxNormalValue
                    ? distance + ReadPacked(exceptionArea, exceptionIndex++, ExceptionBitWidth)
                    : distance;

            bitPosition += fullDistance;
            if (bitPosition > 32)
                return [];

            delta |= 1u << (bitPosition - 1);
        }

        return hashes;
    }

    private static int ReadPacked(ReadOnlySpan<byte> area, int index, int width)
    {
        int value = 0;
        int bit = index * width;

        for (int b = 0; b < width; b++, bit++)
        {
            if ((area[bit >> 3] & (1 << (bit & 7))) != 0)
                value |= 1 << b;
        }

        return value;
    }

    private static byte[]? FromUrlSafeBase64(string encoded)
    {
        if (encoded.Length == 0)
            return null;

        string standard = encoded.Replace('-', '+').Replace('_', '/');
        int padding = (4 - standard.Length % 4) % 4;
        if (padding == 3)
            return null;

        string padded = standard + new string('=', padding);
        byte[] buffer = new byte[padded.Length / 4 * 3];

        return Convert.TryFromBase64String(padded, buffer, out int written)
            ? buffer[..written]
            : null;
    }
}
