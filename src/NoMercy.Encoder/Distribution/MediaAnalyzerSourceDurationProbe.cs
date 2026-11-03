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

using Microsoft.Extensions.Logging;
using NoMercy.Encoder.Analysis;

namespace NoMercy.Encoder.Distribution;

/// <summary>
/// Measures a task's source with the same ffprobe analysis the encode
/// pipeline runs on its inputs. A source that cannot be probed (missing
/// file, unreadable container) yields null: the task still encodes, it
/// just cannot show a percentage.
/// </summary>
public class MediaAnalyzerSourceDurationProbe(
    IMediaAnalyzer mediaAnalyzer,
    ILogger<MediaAnalyzerSourceDurationProbe> logger
) : ISourceDurationProbe
{
    public async Task<TimeSpan?> ProbeAsync(string inputPath, CancellationToken ct)
    {
        try
        {
            MediaInfo info = await mediaAnalyzer.AnalyzeAsync(inputPath, ct);
            return info.Duration > TimeSpan.Zero ? info.Duration : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not measure the duration of {InputPath}", inputPath);
            return null;
        }
    }
}
