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

using ImageMagick;
using NoMercy.Database;

namespace NoMercy.MediaProcessing.Images;

internal static class ColorQuantizer
{
    public const int MaxDimension = 128;
    private const int QuantizationBits = 5;
    private const int QuantizationShift = 8 - QuantizationBits;
    private const int QuantizationMask = (0xFF >> QuantizationShift) << QuantizationShift;
    private const int MaxSwatches = 32;

    private const double MinLuminance = 0.05;
    private const double MaxLuminance = 0.95;
    private const byte MinAlpha = 128;

    private const double SaturationWeight = 3.0;
    private const double LuminanceWeight = 6.5;
    private const double PopulationWeight = 0.5;

    public static PaletteColors ExtractPalette(IMagickImage<byte> image)
    {
        using IMagickImage<byte> workingImage = DownsampleImage(image);
        List<QuantizedColor> pixels = ExtractAndFilterPixels(workingImage);

        if (pixels.Count == 0)
        {
            return EmptyPalette();
        }

        List<ColorSwatch> swatches = MedianCutQuantize(pixels);

        if (swatches.Count == 0)
        {
            return EmptyPalette();
        }

        return ScoreSwatches(swatches);
    }

    private static IMagickImage<byte> DownsampleImage(IMagickImage<byte> image)
    {
        IMagickImage<byte> working = image.Clone();

        if (image is { Width: <= MaxDimension, Height: <= MaxDimension })
        {
            return working;
        }

        double scale = Math.Min(
            (double)MaxDimension / image.Width,
            (double)MaxDimension / image.Height
        );
        uint newWidth = Math.Max(1, (uint)(image.Width * scale));
        uint newHeight = Math.Max(1, (uint)(image.Height * scale));

        // '!' ignores the aspect ratio so the size matches the computed one exactly.
        working.Resize(new MagickGeometry(newWidth, newHeight) { IgnoreAspectRatio = true });
        return working;
    }

    private static List<QuantizedColor> ExtractAndFilterPixels(IMagickImage<byte> image)
    {
        List<QuantizedColor> pixels = [];

        using IPixelCollection<byte> pixelCollection = image.GetPixelsUnsafe();
        byte[] rgba = pixelCollection.ToByteArray(PixelMapping.RGBA) ?? [];

        for (int offset = 0; offset + 3 < rgba.Length; offset += 4)
        {
            byte r = rgba[offset];
            byte g = rgba[offset + 1];
            byte b = rgba[offset + 2];
            byte a = rgba[offset + 3];

            if (a < MinAlpha)
            {
                continue;
            }

            double luminance = GetLuminance(r, g, b);

            if (luminance < MinLuminance || luminance > MaxLuminance)
            {
                continue;
            }

            byte qr = (byte)(r & QuantizationMask);
            byte qg = (byte)(g & QuantizationMask);
            byte qb = (byte)(b & QuantizationMask);

            pixels.Add(new(qr, qg, qb, r, g, b));
        }

        return pixels;
    }

    private static List<ColorSwatch> MedianCutQuantize(List<QuantizedColor> pixels)
    {
        List<ColorBox> boxes = [new(pixels)];

        while (boxes.Count < MaxSwatches)
        {
            ColorBox? largestBox = null;
            int largestIndex = -1;

            for (int i = 0; i < boxes.Count; i++)
            {
                if (
                    boxes[i].CanSplit && (largestBox is null || boxes[i].Volume > largestBox.Volume)
                )
                {
                    largestBox = boxes[i];
                    largestIndex = i;
                }
            }

            if (largestBox is null)
            {
                break;
            }

            (ColorBox left, ColorBox right) = largestBox.Split();
            boxes[largestIndex] = left;
            boxes.Add(right);
        }

        List<ColorSwatch> swatches = [];

        foreach (ColorBox box in boxes)
        {
            if (box.Population > 0)
            {
                swatches.Add(box.ToSwatch());
            }
        }

        return swatches;
    }

    private static PaletteColors ScoreSwatches(List<ColorSwatch> swatches)
    {
        int maxPopulation = 0;
        ColorSwatch dominantSwatch = swatches[0];

        foreach (ColorSwatch swatch in swatches)
        {
            if (swatch.Population > maxPopulation)
            {
                maxPopulation = swatch.Population;
                dominantSwatch = swatch;
            }
        }

        SwatchTarget[] targets =
        [
            new("LightVibrant", 1.0, 0.74),
            new("Vibrant", 1.0, 0.50),
            new("DarkVibrant", 1.0, 0.26),
            new("LightMuted", 0.3, 0.74),
            new("Muted", 0.3, 0.50),
            new("DarkMuted", 0.3, 0.26),
        ];

        Dictionary<string, ColorSwatch?> assigned = new();
        HashSet<int> usedSwatchIndices = [];

        foreach (SwatchTarget target in targets)
        {
            double bestScore = double.MinValue;
            int bestIndex = -1;

            for (int i = 0; i < swatches.Count; i++)
            {
                if (usedSwatchIndices.Contains(i))
                {
                    continue;
                }

                ColorSwatch swatch = swatches[i];
                double satDistance = Math.Abs(swatch.Saturation - target.TargetSaturation);
                double lumDistance = Math.Abs(swatch.Luminance - target.TargetLuminance);
                double popNormalized =
                    maxPopulation > 0 ? (double)swatch.Population / maxPopulation : 0;

                double score =
                    1.0
                    - SaturationWeight * satDistance
                    - LuminanceWeight * lumDistance
                    - PopulationWeight * (1.0 - popNormalized);

                if (score > bestScore)
                {
                    bestScore = score;
                    bestIndex = i;
                }
            }

            if (bestIndex >= 0)
            {
                assigned[target.Name] = swatches[bestIndex];
                usedSwatchIndices.Add(bestIndex);
            }
            else
            {
                assigned[target.Name] = null;
            }
        }

        ColorSwatch? vibrant = assigned.GetValueOrDefault("Vibrant");
        ColorSwatch? muted = assigned.GetValueOrDefault("Muted");
        ColorSwatch primarySwatch = vibrant ?? muted ?? dominantSwatch;

        return new()
        {
            Dominant = SwatchToHex(dominantSwatch),
            Primary = SwatchToHex(primarySwatch),
            LightVibrant = SwatchToHex(
                assigned.GetValueOrDefault("LightVibrant") ?? dominantSwatch
            ),
            DarkVibrant = SwatchToHex(assigned.GetValueOrDefault("DarkVibrant") ?? dominantSwatch),
            LightMuted = SwatchToHex(assigned.GetValueOrDefault("LightMuted") ?? dominantSwatch),
            DarkMuted = SwatchToHex(assigned.GetValueOrDefault("DarkMuted") ?? dominantSwatch),
        };
    }

    private static string SwatchToHex(ColorSwatch swatch)
    {
        return $"#{swatch.R:X2}{swatch.G:X2}{swatch.B:X2}FF";
    }

    private static PaletteColors EmptyPalette()
    {
        return new()
        {
            Dominant = "#808080FF",
            Primary = "#808080FF",
            LightVibrant = "#C0C0C0FF",
            DarkVibrant = "#404040FF",
            LightMuted = "#C0C0C0FF",
            DarkMuted = "#404040FF",
        };
    }

    private static double GetLuminance(byte r, byte g, byte b)
    {
        double rNorm = r / 255.0;
        double gNorm = g / 255.0;
        double bNorm = b / 255.0;

        double rLinear = rNorm <= 0.04045 ? rNorm / 12.92 : Math.Pow((rNorm + 0.055) / 1.055, 2.4);
        double gLinear = gNorm <= 0.04045 ? gNorm / 12.92 : Math.Pow((gNorm + 0.055) / 1.055, 2.4);
        double bLinear = bNorm <= 0.04045 ? bNorm / 12.92 : Math.Pow((bNorm + 0.055) / 1.055, 2.4);

        return 0.2126 * rLinear + 0.7152 * gLinear + 0.0722 * bLinear;
    }

    private static (double saturation, double luminance) GetHsl(byte r, byte g, byte b)
    {
        double rNorm = r / 255.0;
        double gNorm = g / 255.0;
        double bNorm = b / 255.0;

        double max = Math.Max(rNorm, Math.Max(gNorm, bNorm));
        double min = Math.Min(rNorm, Math.Min(gNorm, bNorm));
        double delta = max - min;

        double luminance = (max + min) / 2.0;

        double saturation;

        if (delta == 0)
        {
            saturation = 0;
        }
        else
        {
            saturation = luminance <= 0.5 ? delta / (max + min) : delta / (2.0 - max - min);
        }

        return (saturation, luminance);
    }

    private readonly struct QuantizedColor(
        byte qr,
        byte qg,
        byte qb,
        byte origR,
        byte origG,
        byte origB
    )
    {
        public byte Qr { get; } = qr;
        public byte Qg { get; } = qg;
        public byte Qb { get; } = qb;
        public byte OrigR { get; } = origR;
        public byte OrigG { get; } = origG;
        public byte OrigB { get; } = origB;
    }

    private readonly struct ColorSwatch(
        byte r,
        byte g,
        byte b,
        int population,
        double saturation,
        double luminance
    )
    {
        public byte R { get; } = r;
        public byte G { get; } = g;
        public byte B { get; } = b;
        public int Population { get; } = population;
        public double Saturation { get; } = saturation;
        public double Luminance { get; } = luminance;
    }

    private readonly struct SwatchTarget(
        string name,
        double targetSaturation,
        double targetLuminance
    )
    {
        public string Name { get; } = name;
        public double TargetSaturation { get; } = targetSaturation;
        public double TargetLuminance { get; } = targetLuminance;
    }

    private sealed class ColorBox
    {
        private readonly List<QuantizedColor> _pixels;
        private readonly byte _minR,
            _maxR,
            _minG,
            _maxG,
            _minB,
            _maxB;

        public ColorBox(List<QuantizedColor> pixels)
        {
            _pixels = pixels;

            byte minR = 255,
                maxR = 0;
            byte minG = 255,
                maxG = 0;
            byte minB = 255,
                maxB = 0;

            foreach (QuantizedColor pixel in pixels)
            {
                if (pixel.Qr < minR)
                    minR = pixel.Qr;
                if (pixel.Qr > maxR)
                    maxR = pixel.Qr;
                if (pixel.Qg < minG)
                    minG = pixel.Qg;
                if (pixel.Qg > maxG)
                    maxG = pixel.Qg;
                if (pixel.Qb < minB)
                    minB = pixel.Qb;
                if (pixel.Qb > maxB)
                    maxB = pixel.Qb;
            }

            _minR = minR;
            _maxR = maxR;
            _minG = minG;
            _maxG = maxG;
            _minB = minB;
            _maxB = maxB;
        }

        public int Population => _pixels.Count;

        public int Volume
        {
            get
            {
                int rangeR = _maxR - _minR;
                int rangeG = _maxG - _minG;
                int rangeB = _maxB - _minB;
                return (rangeR + 1) * (rangeG + 1) * (rangeB + 1);
            }
        }

        public bool CanSplit => _pixels.Count >= 2;

        public (ColorBox left, ColorBox right) Split()
        {
            int rangeR = _maxR - _minR;
            int rangeG = _maxG - _minG;
            int rangeB = _maxB - _minB;

            Channel longestChannel;

            if (rangeR >= rangeG && rangeR >= rangeB)
            {
                longestChannel = Channel.R;
            }
            else if (rangeG >= rangeR && rangeG >= rangeB)
            {
                longestChannel = Channel.G;
            }
            else
            {
                longestChannel = Channel.B;
            }

            _pixels.Sort(
                (a, b) =>
                    longestChannel switch
                    {
                        Channel.R => a.Qr.CompareTo(b.Qr),
                        Channel.G => a.Qg.CompareTo(b.Qg),
                        Channel.B => a.Qb.CompareTo(b.Qb),
                        _ => 0,
                    }
            );

            int median = _pixels.Count / 2;

            List<QuantizedColor> leftPixels = _pixels.GetRange(0, median);
            List<QuantizedColor> rightPixels = _pixels.GetRange(median, _pixels.Count - median);

            return (new(leftPixels), new(rightPixels));
        }

        public ColorSwatch ToSwatch()
        {
            long totalR = 0,
                totalG = 0,
                totalB = 0;

            foreach (QuantizedColor pixel in _pixels)
            {
                totalR += pixel.OrigR;
                totalG += pixel.OrigG;
                totalB += pixel.OrigB;
            }

            byte avgR = (byte)(totalR / _pixels.Count);
            byte avgG = (byte)(totalG / _pixels.Count);
            byte avgB = (byte)(totalB / _pixels.Count);

            (double saturation, double luminance) = GetHsl(avgR, avgG, avgB);

            return new(avgR, avgG, avgB, _pixels.Count, saturation, luminance);
        }

        private enum Channel
        {
            R,
            G,
            B,
        }
    }
}
