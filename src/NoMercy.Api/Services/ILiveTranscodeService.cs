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
using NoMercy.Api.Controllers.V1.Streaming.Dtos;

namespace NoMercy.Api.Services;

/// <summary>
/// Live-transcode orchestration lifted out of
/// <c>LiveTranscodeController</c>: session lifecycle, playlist/segment
/// production, quality changes and seeking. The controller keeps only auth
/// checks and HTTP result mapping.
/// </summary>
public interface ILiveTranscodeService
{
    /// <summary>
    /// The caller's own sessions; every session on the server when
    /// <paramref name="includeAll"/> is set (moderators and the owner).
    /// </summary>
    IReadOnlyList<LiveSessionDto> ListSessions(Guid userId, bool includeAll);

    Task<LiveResult> StartSessionAsync(
        Guid userId,
        StartLiveSessionRequest request,
        string? deviceId,
        CancellationToken ct
    );

    LiveResult GetMasterPlaylist(Guid userId, string sessionId);

    LiveResult GetPlaylist(Guid userId, string sessionId);

    Task<LiveResult> GetSegmentAsync(
        Guid userId,
        string sessionId,
        string epoch,
        int index,
        CancellationToken ct
    );

    LiveResult ReportPosition(Guid userId, string sessionId, ReportPositionRequest request);

    /// <summary>
    /// REST fallback for reporting client network health (download-buffer
    /// depth + observed downlink) — the SignalR equivalent is
    /// <c>LiveTranscodeHub.ReportBufferHealth</c>.
    /// </summary>
    LiveResult ReportBufferHealth(Guid userId, string sessionId, ReportBufferHealthRequest request);

    Task<LiveResult> ChangeQualityAsync(
        Guid userId,
        string sessionId,
        ChangeQualityRequest request,
        CancellationToken ct
    );

    Task<LiveResult> SeekAsync(
        Guid userId,
        string sessionId,
        SeekRequest request,
        CancellationToken ct
    );

    Task<LiveResult> EndSessionAsync(Guid userId, string sessionId, CancellationToken ct);
}
