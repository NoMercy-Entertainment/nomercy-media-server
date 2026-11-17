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

namespace NoMercy.Encoder.ContentAnalysis.Segments;

/// <summary>
/// Tiny grey frames (<see cref="SegmentRules.ThumbnailWidth"/> x
/// <see cref="SegmentRules.ThumbnailHeight"/>, 2 per second) of one episode,
/// sorted by time. Frame <c>i</c> is <see cref="SegmentRules.ThumbnailPixels"/>
/// bytes at <c>i * ThumbnailPixels</c> in <see cref="Pixels"/>.
/// </summary>
public sealed class ThumbnailStrip
{
    public ThumbnailStrip(double[] times, byte[] pixels)
    {
        if (pixels.Length != times.Length * SegmentRules.ThumbnailPixels)
            throw new ArgumentException(
                "pixel buffer does not hold one frame per time",
                nameof(pixels)
            );

        Times = times;
        Pixels = pixels;
    }

    public double[] Times { get; }

    public byte[] Pixels { get; }

    public int Count => Times.Length;

    public ReadOnlySpan<byte> Frame(int index) =>
        Pixels.AsSpan(index * SegmentRules.ThumbnailPixels, SegmentRules.ThumbnailPixels);

    /// <summary>Strips merged and sorted by time; the first strip wins a duplicate time.</summary>
    public static ThumbnailStrip Merge(IEnumerable<ThumbnailStrip> strips)
    {
        List<(double Time, ThumbnailStrip Strip, int Index)> frames = [];
        foreach (ThumbnailStrip strip in strips)
        {
            for (int i = 0; i < strip.Count; i++)
                frames.Add((strip.Times[i], strip, i));
        }

        List<(double Time, ThumbnailStrip Strip, int Index)> ordered =
        [
            .. frames.OrderBy(f => f.Time),
        ];

        List<double> times = [];
        List<byte> pixels = [];
        foreach ((double time, ThumbnailStrip strip, int index) in ordered)
        {
            if (times.Count > 0 && times[^1].Equals(time))
                continue;
            times.Add(time);
            pixels.AddRange(strip.Frame(index).ToArray());
        }

        return new([.. times], [.. pixels]);
    }
}
