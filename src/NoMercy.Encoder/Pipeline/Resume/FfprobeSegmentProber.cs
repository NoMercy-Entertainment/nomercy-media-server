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
using NoMercy.Encoder.Composition;
using NoMercy.Encoder.Infrastructure;

namespace NoMercy.Encoder.Pipeline.Resume;

public class FfprobeSegmentProber(IProcessRunner processRunner, EncoderOptions options)
    : ISegmentProber
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);

    public async Task<double?> FirstPacketPtsAsync(
        string segmentPath,
        bool video,
        CancellationToken ct
    )
    {
        string[] arguments =
        [
            "-v",
            "quiet",
            "-select_streams",
            video ? "v:0" : "a:0",
            "-show_entries",
            "packet=pts_time",
            "-of",
            "csv=p=0",
            "-read_intervals",
            "%+#1",
            segmentPath,
        ];

        ProcessResult result = await processRunner.RunAsync(
            options.FfprobePath,
            arguments,
            ProbeTimeout,
            cancellationToken: ct
        );

        return result.IsSuccess ? ParseFirstPts(result.StdOut) : null;
    }

    internal static double? ParseFirstPts(string output)
    {
        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string first = line.Split(',')[0].Trim();
            if (
                double.TryParse(
                    first,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out double pts
                )
            )
                return pts;
        }

        return null;
    }
}
