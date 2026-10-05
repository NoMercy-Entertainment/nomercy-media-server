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

using NoMercy.Encoder.Errors;
using NoMercy.Encoder.Pipeline;

namespace NoMercy.Tests.Encoder.Orchestration;

public class EncodingResultTests
{
    [Fact]
    public void Default_construction_has_success_status_and_empty_collections()
    {
        EncodingResult result = new(
            Success: true,
            OutputPath: "/out/test",
            Duration: TimeSpan.FromSeconds(10),
            Error: null,
            Metrics: null
        );

        Assert.Equal("success", result.Status);
        Assert.Equal("", result.JobId);
        Assert.Null(result.Plan);
        Assert.Empty(result.Artifacts);
        Assert.Null(result.Stats);
        Assert.Empty(result.Warnings);
        Assert.Null(result.EnrichedError);
        Assert.True(result.Success);
        Assert.Equal("/out/test", result.OutputPath);
    }

    [Fact]
    public void Failed_status_carries_error_shape_with_catalogued_id()
    {
        EncoderErrorShape shape = new(
            Id: EncoderRuleId.EncoderInitFailed,
            Message: "Encoder 'h264_nvenc' failed to initialise: no device found",
            Suggestion: "Check ffmpeg capability probe.",
            Details: null
        );
        EncodingError legacyError = new(
            Kind: EncodingErrorKind.Unknown,
            Message: shape.Message,
            FfmpegStderr: null,
            StageName: "Test",
            Recoverable: false
        );

        EncodingResult result = new(
            Success: false,
            OutputPath: string.Empty,
            Duration: TimeSpan.Zero,
            Error: legacyError,
            Metrics: null
        )
        {
            Status = "failed",
            EnrichedError = shape,
        };

        Assert.Equal("failed", result.Status);
        Assert.False(result.Success);
        Assert.NotNull(result.EnrichedError);
        Assert.Equal(EncoderRuleId.EncoderInitFailed, result.EnrichedError!.Id);
        Assert.Contains("h264_nvenc", result.EnrichedError.Message);
        Assert.Empty(result.Artifacts);
        Assert.Null(result.Stats);
    }

    [Fact]
    public void Cancelled_status_has_no_artifacts_or_stats()
    {
        EncodingResult result = new(
            Success: false,
            OutputPath: string.Empty,
            Duration: TimeSpan.FromSeconds(3),
            Error: null,
            Metrics: null
        )
        {
            Status = "cancelled",
        };

        Assert.Equal("cancelled", result.Status);
        Assert.False(result.Success);
        Assert.Empty(result.Artifacts);
        Assert.Null(result.Stats);
        Assert.Null(result.EnrichedError);
        Assert.Null(result.Plan);
    }

    [Fact]
    public void Status_round_trips_via_with_expression()
    {
        EncodingResult original = new(
            Success: true,
            OutputPath: "/out/a",
            Duration: TimeSpan.FromMinutes(1),
            Error: null,
            Metrics: null
        )
        {
            Status = "success",
            JobId = "job-001",
        };

        EncodingResult enriched = original with
        {
            Stats = new(
                DurationSeconds: 60.0,
                AvgFps: 30.0,
                OutputBitrateKbps: 4000,
                SourceBytes: 1_000_000_000L,
                OutputBytes: 500_000_000L
            ),
            Artifacts =
            [
                new(
                    "/out/a/master.m3u8",
                    1024L,
                    "abc123",
                    "application/vnd.apple.mpegurl"
                ),
            ],
        };

        // Original unchanged
        Assert.Equal("success", original.Status);
        Assert.Equal("job-001", original.JobId);
        Assert.Empty(original.Artifacts);
        Assert.Null(original.Stats);

        // Enriched carries new fields
        Assert.Equal("success", enriched.Status);
        Assert.Equal("job-001", enriched.JobId);
        Assert.Single(enriched.Artifacts);
        Assert.NotNull(enriched.Stats);
        Assert.Equal(60.0, enriched.Stats!.DurationSeconds);
        Assert.Equal(30.0, enriched.Stats.AvgFps);
    }
}
