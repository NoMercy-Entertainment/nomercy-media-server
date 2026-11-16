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

    public Task<double?> FirstPacketPtsAsync(
        string segmentPath,
        bool video,
        CancellationToken ct
    ) => ProbeAsync(segmentPath, video ? "v:0" : "a:0", ct);

    public Task<double?> FirstPtsAsync(string segmentPath, CancellationToken ct) =>
        ProbeAsync(segmentPath, null, ct);

    public async Task<double?> FirstPacketDelayAsync(
        string segmentPath,
        bool video,
        CancellationToken ct
    )
    {
        ProcessResult result = await RunProbeAsync(
            segmentPath,
            video ? "v:0" : "a:0",
            "packet=pts_time,dts_time",
            ct
        );

        return result.IsSuccess ? ParseFirstDelay(result.StdOut) : null;
    }

    private async Task<double?> ProbeAsync(string segmentPath, string? stream, CancellationToken ct)
    {
        ProcessResult result = await RunProbeAsync(segmentPath, stream, "packet=pts_time", ct);

        return result.IsSuccess ? ParseFirstPts(result.StdOut) : null;
    }

    private async Task<ProcessResult> RunProbeAsync(
        string segmentPath,
        string? stream,
        string entries,
        CancellationToken ct
    )
    {
        string[] arguments =
        [
            "-v",
            "quiet",
            .. stream is null ? Array.Empty<string>() : ["-select_streams", stream],
            "-show_entries",
            entries,
            "-of",
            "csv=p=0",
            "-read_intervals",
            "%+#1",
            segmentPath,
        ];

        return await processRunner.RunAsync(
            options.FfprobePath,
            arguments,
            ProbeTimeout,
            cancellationToken: ct
        );
    }

    internal static double? ParseFirstDelay(string output)
    {
        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] fields = line.Split(',');
            if (
                fields.Length >= 2
                && double.TryParse(
                    fields[0].Trim(),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out double pts
                )
                && double.TryParse(
                    fields[1].Trim(),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out double dts
                )
            )
                return pts - dts;
        }

        return null;
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
