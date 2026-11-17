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

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace NoMercy.Tests.Encoder.ContentAnalysis.Segments.Support;

/// <summary>
/// Reads the research prototype's thumbnails (<c>np.savez_compressed</c>: a zip of
/// <c>t.npy</c>, float64 times, and <c>px.npy</c>, uint8 frames of 576 pixels).
/// Only the two array types the prototype wrote are supported.
/// </summary>
public static partial class NpzReader
{
    public static (double[] Times, byte[] Pixels) ReadThumbnails(string path)
    {
        using ZipArchive zip = ZipFile.OpenRead(path);
        (string timesType, int[] _, byte[] timesData) = ReadArray(zip, "t.npy");
        (string pixelsType, int[] _, byte[] pixelsData) = ReadArray(zip, "px.npy");
        if (timesType != "<f8" || pixelsType != "|u1")
            throw new InvalidDataException($"unexpected npz dtypes {timesType} / {pixelsType}");

        double[] times = new double[timesData.Length / sizeof(double)];
        for (int i = 0; i < times.Length; i++)
            times[i] = BinaryPrimitives.ReadDoubleLittleEndian(timesData.AsSpan(i * sizeof(double)));

        return (times, pixelsData);
    }

    private static (string Dtype, int[] Shape, byte[] Data) ReadArray(ZipArchive zip, string name)
    {
        ZipArchiveEntry entry =
            zip.GetEntry(name) ?? throw new InvalidDataException($"npz has no {name}");
        using Stream stream = entry.Open();
        using MemoryStream buffer = new();
        stream.CopyTo(buffer);
        byte[] bytes = buffer.ToArray();

        // \x93NUMPY, version major/minor, header length (2 bytes in v1, 4 in v2+).
        int major = bytes[6];
        int headerLength = major == 1
            ? BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(8))
            : (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8));
        int headerStart = major == 1 ? 10 : 12;
        string header = Encoding.ASCII.GetString(bytes, headerStart, headerLength);

        string dtype = DescrRegex().Match(header).Groups[1].Value;
        int[] shape =
        [
            .. ShapeRegex()
                .Match(header)
                .Groups[1]
                .Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(int.Parse),
        ];
        return (dtype, shape, bytes[(headerStart + headerLength)..]);
    }

    [GeneratedRegex(@"'descr':\s*'([^']+)'")]
    private static partial Regex DescrRegex();

    [GeneratedRegex(@"'shape':\s*\(([^)]*)\)")]
    private static partial Regex ShapeRegex();
}
