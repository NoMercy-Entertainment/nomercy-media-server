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
using NoMercy.Api.Controllers.V1.Streaming.Dtos;
using NoMercy.Api.Services;
using NoMercy.Authorization;

namespace NoMercy.Api.Controllers.V1.Streaming;

[ApiController]
[Tags("Streaming")]
[ApiVersion(1.0)]
[Authorize(Policy = "MediaAccess")]
[Route("api/v{version:apiVersion}/streaming/live")]
public class LiveTranscodeController(ILiveTranscodeService service) : BaseController
{
    [HttpGet("sessions")]
    public IActionResult ListSessions()
    {
        return Ok(service.ListSessions(User.UserId(), includeAll: AuthPolicy.IsModerator(User)));
    }

    [HttpPost("sessions")]
    public async Task<IActionResult> StartSession(
        [FromBody] StartLiveSessionRequest request,
        [FromQuery] string? deviceId = null,
        CancellationToken ct = default
    )
    {
        return MapResult(await service.StartSessionAsync(User.UserId(), request, deviceId, ct));
    }

    [HttpGet("sessions/{sessionId}/master.m3u8")]
    public IActionResult GetMasterPlaylist(string sessionId)
    {
        LiveResult result = service.GetMasterPlaylist(User.UserId(), sessionId);
        if (result.Kind != LiveResultKind.Ok)
            return MapResult(result);

        return Content((string)result.Payload!, "application/vnd.apple.mpegurl");
    }

    [HttpGet("sessions/{sessionId}/playlist.m3u8")]
    public IActionResult GetPlaylist(string sessionId)
    {
        LiveResult result = service.GetPlaylist(User.UserId(), sessionId);
        if (result.Kind != LiveResultKind.Ok)
            return MapResult(result);

        return Content((string)result.Payload!, "application/vnd.apple.mpegurl");
    }

    [HttpGet("sessions/{sessionId}/segment/{epoch}/{index:int}.ts")]
    public async Task<IActionResult> GetSegment(
        string sessionId,
        string epoch,
        int index,
        CancellationToken ct = default
    )
    {
        LiveResult result = await service.GetSegmentAsync(
            User.UserId(),
            sessionId,
            epoch,
            index,
            ct
        );
        if (result.Kind != LiveResultKind.Ok)
            return MapResult(result);

        Response.Headers["Accept-Ranges"] = "bytes";
        return File((Stream)result.Payload!, "video/mp2t", enableRangeProcessing: true);
    }

    [HttpPost("sessions/{sessionId}/position")]
    public IActionResult ReportPosition(string sessionId, [FromBody] ReportPositionRequest request)
    {
        return MapResult(service.ReportPosition(User.UserId(), sessionId, request));
    }

    /// <summary>
    /// REST fallback for clients that don't use the SignalR
    /// <c>LiveTranscodeHub.ReportBufferHealth</c> method to report their
    /// download-buffer depth and observed downlink.
    /// </summary>
    [HttpPost("sessions/{sessionId}/buffer-health")]
    public IActionResult ReportBufferHealth(
        string sessionId,
        [FromBody] ReportBufferHealthRequest request
    )
    {
        return MapResult(service.ReportBufferHealth(User.UserId(), sessionId, request));
    }

    [HttpPost("sessions/{sessionId}/quality")]
    public async Task<IActionResult> ChangeQuality(
        string sessionId,
        [FromBody] ChangeQualityRequest request,
        CancellationToken ct = default
    )
    {
        return MapResult(await service.ChangeQualityAsync(User.UserId(), sessionId, request, ct));
    }

    [HttpPost("sessions/{sessionId}/seek")]
    public async Task<IActionResult> Seek(
        string sessionId,
        [FromBody] SeekRequest request,
        CancellationToken ct = default
    )
    {
        return MapResult(await service.SeekAsync(User.UserId(), sessionId, request, ct));
    }

    [HttpDelete("sessions/{sessionId}")]
    public async Task<IActionResult> EndSession(string sessionId)
    {
        LiveResult result = await service.EndSessionAsync(
            User.UserId(),
            sessionId,
            HttpContext.RequestAborted
        );
        if (result.Kind != LiveResultKind.Ok)
            return MapResult(result);

        return NoContent();
    }

    private IActionResult MapResult(LiveResult result) =>
        result.Kind switch
        {
            LiveResultKind.Ok => Ok(result.Payload),
            LiveResultKind.BadRequest => BadRequestResponse(result.Message!),
            LiveResultKind.NotFound => NotFoundResponse(result.Message!),
            LiveResultKind.Gone => GoneResponse(result.Message!),
            LiveResultKind.ServiceUnavailable => ServiceUnavailableResponse(result.Message!),
            LiveResultKind.InternalError => InternalServerErrorResponse(result.Message!),
            LiveResultKind.EncoderError => StatusCode(
                result.EncoderStatusCode,
                result.EncoderShape
            ),
            _ => InternalServerErrorResponse("Unexpected result"),
        };
}
