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

using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using NoMercy.Encoder.Codecs;
using NoMercy.Encoder.Execution;
using NoMercy.Encoder.Hardware;
using NoMercy.Encoder.Startup;
using NoMercy.Resources;

namespace NoMercy.Api.Controllers.V1.Encoder;

/// <summary>
/// Endpoints for triggering on-demand hardware benchmarks and polling their
/// results, and for querying live resource utilization. All operations require
/// the requesting user to be a moderator (owner or manager).
///
/// NOTE: The underlying <see cref="IHardwareBenchmark.CalibrateAsync"/> does
/// not currently accept codec or resolution filters — the engine always
/// calibrates every available codec. The <c>codecs</c> and <c>resolutions</c>
/// fields in the request body are recorded in the job status for
/// observability and will drive actual filtering once the benchmark engine
/// is extended.
/// </summary>
[ApiController]
[Tags("Encoder Hardware")]
[ApiVersion(1.0)]
[Authorize(Policy = "Moderator")]
[Route("api/v{version:apiVersion}/encoder/hardware")]
public class EncoderHardwareController(
    IBenchmarkJobTracker tracker,
    IResourceMonitor monitor,
    IEncoderProcessRegistry registry,
    IHardwareCapabilities hardware,
    IFfmpegCapabilityProbe probe
) : BaseController
{
    /// <summary>
    /// Starts a new benchmark calibration run asynchronously.
    /// Returns 202 Accepted immediately with a job_id for polling.
    /// Invalid codec names produce 422.
    /// </summary>
    [HttpPost("benchmark")]
    public IActionResult StartBenchmark([FromBody] StartBenchmarkRequest? request)
    {
        List<VideoCodecType> codecs = [];

        if (request?.Codecs is { Length: > 0 } rawCodecs)
        {
            foreach (string name in rawCodecs)
            {
                if (!Enum.TryParse(name, ignoreCase: true, out VideoCodecType parsed))
                {
                    return UnprocessableEntity(
                        new
                        {
                            error = "benchmark.invalid_codec",
                            message = $"Unknown codec name '{name}'.",
                            suggestion = $"Valid values are: {string.Join(", ", Enum.GetNames<VideoCodecType>())}.",
                        }
                    );
                }

                codecs.Add(parsed);
            }
        }

        List<int> resolutions = request?.Resolutions?.ToList() ?? [];

        BenchmarkJobStatus job = tracker.Start(codecs, resolutions);

        return Accepted(new { job_id = job.JobId, status = job.Status });
    }

    /// <summary>
    /// Returns the current status of a benchmark job.
    /// Poll this endpoint until <c>status</c> is "completed", "failed", or
    /// "cancelled".
    /// </summary>
    [HttpGet("benchmark/{jobId}")]
    public IActionResult GetBenchmark(string jobId)
    {
        BenchmarkJobStatus? job = tracker.Get(jobId);
        if (job is null)
            return NotFoundResponse($"No benchmark job with id '{jobId}' found");

        return Ok(job);
    }

    /// <summary>
    /// Returns all known benchmark jobs for this process lifetime.
    /// History is lost on server restart; the durable SpeedIndex is
    /// available at GET /api/v1/dashboard/hardware/benchmark.
    /// </summary>
    [HttpGet("benchmark")]
    public IActionResult ListBenchmarks()
    {
        return Ok(new { data = tracker.List() });
    }

    /// <summary>
    /// Returns a live snapshot of host resource utilization: CPU load, available
    /// memory, per-process GPU encoder samples (empty when no vendor telemetry
    /// plugin is installed), and the count of concurrent NVENC sessions currently
    /// tracked by the process registry.
    /// </summary>
    [HttpGet("utilization")]
    public async Task<IActionResult> GetUtilization()
    {
        UtilizationSnapshot snap = new(
            CpuUsagePercent: monitor.GetCpuUsagePercent(),
            AvailableMemoryMb: monitor.GetAvailableMemoryMb(),
            GpuSamples: await monitor.SampleGpuAsync(),
            ConcurrentNvencSessions: registry.CountConcurrentNvencSessions(),
            Gpus: hardware.Gpus
        );

        return Ok(snap);
    }

    /// <summary>
    /// Returns the cached FFmpeg capability report: protocol support (BluRay,
    /// DVD), available encoders, missing filters and muxers, and optional-tool
    /// presence (fpcalc, Whisper model, stemsplit model, Tesseract eng.traineddata).
    /// Returns <c>probe_pending</c> if the background probe has not yet completed.
    /// </summary>
    [HttpGet("/api/v{version:apiVersion}/encoder/capabilities")]
    public IActionResult GetCapabilities()
    {
        CapabilityReport? report = probe.GetCachedReport();
        return report is null
            ? Ok(
                new
                {
                    status = "probe_pending",
                    message = "Capability probe has not completed yet.",
                }
            )
            : Ok(report);
    }
}

public record StartBenchmarkRequest(
    [property: JsonProperty("codecs")] string[]? Codecs,
    [property: JsonProperty("resolutions")] int[]? Resolutions
);

/// <summary>
/// Point-in-time resource utilization snapshot returned by
/// <c>GET /api/v1/encoder/hardware/utilization</c>.
/// </summary>
public record UtilizationSnapshot(
    [property: JsonProperty("cpu_usage_percent")] double CpuUsagePercent,
    [property: JsonProperty("available_memory_mb")] long AvailableMemoryMb,
    [property: JsonProperty("gpu_samples")] IReadOnlyList<GpuProcessSample> GpuSamples,
    [property: JsonProperty("concurrent_nvenc_sessions")] int ConcurrentNvencSessions,
    [property: JsonProperty("gpus")] IReadOnlyList<GpuDevice> Gpus
);
