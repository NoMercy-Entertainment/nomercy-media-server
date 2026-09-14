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
using Microsoft.Extensions.Logging;
using NoMercy.Data.Repositories;
using NoMercy.Encoder.Composition;
using NoMercy.Encoder.Distribution;
using NoMercy.Storage;

namespace NoMercy.Api.Controllers.V1.Worker;

/// <summary>
/// Coordinator-side endpoint that streams task source files to remote
/// workers over HTTP. Called by <c>HttpSourceFetcher</c> when a worker
/// can't see the original path on its own filesystem (WAN deployment,
/// no shared NAS, etc.).
///
/// Security:
///   1. Signature: <c>sig</c> is HMAC-SHA256 over <c>{path}|{ts}</c>
///      using the shared <see cref="EncoderOptions.DistributedEncodingSigningKey"/>.
///      Rejects anything that doesn't verify.
///   2. Freshness: <c>ts</c> must be within 5 minutes of now. Stops
///      someone capturing a signed URL and replaying it days later.
///   3. Path allowlist: the path must correspond to a known VideoFile
///      in the library. Prevents a coordinator with a leaked signing
///      key from being used as a general file-read oracle.
///
/// Returns 503 when distributed encoding isn't enabled.
/// </summary>
[ApiController]
[Tags("Worker Source")]
[ApiVersion(1.0)]
[AllowAnonymous]
// Primary route per the encoder spec.
[Route("api/v{version:apiVersion}/worker/source")]
// Legacy alias — kept for backwards compatibility with workers on older builds.
[Obsolete("Use /api/v{version}/worker/source — kept for backwards compatibility")]
[Route("api/v{version:apiVersion}/worker-source")]
public class WorkerSourceController(
    IVideoFileRepository videoFileRepository,
    EncoderOptions encoderOptions,
    ILogger<WorkerSourceController> logger,
    IStorage storage
) : BaseController
{
    [HttpGet]
    public async Task<IActionResult> Stream(
        [FromQuery] string path,
        [FromQuery] long ts,
        [FromQuery] string sig,
        CancellationToken ct
    )
    {
        if (!encoderOptions.IsDistributedEncodingEnabled)
            return ServiceUnavailableResponse(
                "Distributed encoding is not enabled on this server."
            );

        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(sig))
            return BadRequestResponse("path and sig query parameters are required");

        if (!SourcePathSignature.IsFresh(ts, DateTimeOffset.UtcNow))
        {
            logger.LogWarning("Rejected worker-source request: signature too old");
            return UnauthenticatedResponse("signature expired");
        }

        byte[] key = encoderOptions.GetDistributedEncodingSigningKey();
        if (!SourcePathSignature.Matches(key, path, ts, sig))
        {
            logger.LogWarning(
                "Rejected worker-source request: signature mismatch for path {Path}",
                path
            );
            return UnauthenticatedResponse("signature invalid");
        }

        // Only paths the library already knows are served, so a leaked signing key
        // cannot turn this endpoint into a general file-read oracle.
        bool isKnownFile = await videoFileRepository.ExistsAtHostPathAsync(
            path.Replace('\\', '/'),
            ct
        );

        if (!isKnownFile)
        {
            logger.LogWarning(
                "Rejected worker-source request: path {Path} not in VideoFiles table",
                path
            );
            return NotFoundResponse("Source file not found in library");
        }

        if (!storage.Exists(path))
        {
            logger.LogWarning(
                "Known VideoFile {Path} is missing on disk (deleted since scan?)",
                path
            );
            return NotFoundResponse("Source file missing on disk");
        }

        // PhysicalFile streams without buffering into memory. enableRangeProcessing
        // = true lets the worker issue Range requests for resume after
        // partial downloads.
        return PhysicalFile(
            path,
            contentType: "application/octet-stream",
            fileDownloadName: Path.GetFileName(path),
            enableRangeProcessing: true
        );
    }
}
