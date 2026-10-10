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

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NoMercy.Data.Activity;
using NoMercy.Database;
using NoMercy.Encoder.LiveTranscode;
using NoMercy.Networking;
using NoMercy.Networking.Messaging;

namespace NoMercy.Api.Hubs;

[Authorize]
public class LiveTranscodeHub(
    IHttpContextAccessor httpContextAccessor,
    IDbContextFactory<MediaContext> contextFactory,
    ConnectedClients connectedClients,
    IActivityLogger activityLogger,
    ISessionManager sessionManager,
    ILiveStreamingService streamingService,
    ILiveSessionPresenceTracker presenceTracker,
    ILogger<LiveTranscodeHub> logger
) : ConnectionHub(httpContextAccessor, contextFactory, connectedClients, activityLogger)
{
    public static string GroupName(string sessionId) => $"live-{sessionId}";

    /// <summary>
    /// Client calls this after receiving the session id from POST /sessions to
    /// start receiving server-push events for that session. Validates that the
    /// calling user owns the session before admitting them to the group.
    /// </summary>
    public async Task<HubCommandResult> SubscribeToSession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return HubCommandResult.Invalid("Session id is required.");

        string? ownerId = sessionManager.GetOwnerUserId(sessionId);

        if (string.IsNullOrWhiteSpace(ownerId))
        {
            logger.LogDebug("SubscribeToSession: session {SessionId} not found", sessionId);
            return HubCommandResult.NotFound("Session was not found.");
        }

        string callerId = Context.UserIdentifier ?? string.Empty;

        if (!string.Equals(ownerId, callerId, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning(
                "SubscribeToSession: caller {CallerId} is not the owner of session {SessionId}",
                callerId,
                sessionId
            );
            return HubCommandResult.Forbidden("Session does not belong to the caller.");
        }

        return await HubCommandResult.ExecuteAsync(
            async () =>
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(sessionId));
                presenceTracker.OnSubscribed(Context.ConnectionId, sessionId);

                logger.LogDebug(
                    "Client {ConnectionId} subscribed to live session {SessionId}",
                    Context.ConnectionId,
                    sessionId
                );
            },
            logger
        );
    }

    /// <summary>
    /// Client leaves the session group. Counterpart to <see cref="SubscribeToSession"/>.
    /// </summary>
    public async Task<HubCommandResult> UnsubscribeFromSession(string sessionId)
    {
        HubCommandResult? error = ValidateSession(sessionId);
        if (error is not null)
            return error;

        return await HubCommandResult.ExecuteAsync(
            async () =>
            {
                presenceTracker.OnUnsubscribed(Context.ConnectionId, sessionId);
                await Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName(sessionId));
            },
            logger
        );
    }

    /// <summary>
    /// A watching connection dropped (tab close, navigation, network loss). Hand
    /// off to the presence tracker, which disposes the connection's sessions once
    /// a short grace window elapses without a reconnect re-subscribing.
    /// </summary>
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        presenceTracker.OnConnectionClosed(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Client heartbeat — updates the session last-activity timestamp so the
    /// idle reaper does not evict an active session.
    /// </summary>
    public HubCommandResult Heartbeat(string sessionId)
    {
        HubCommandResult? error = GetRuntimeError(sessionId, out LiveRuntimeSession runtime);
        if (error is not null)
            return error;

        return HubCommandResult.Execute(runtime.TouchLastAccess, logger);
    }

    /// <summary>
    /// The watching client reports its true playback position, so buffer-ahead
    /// is measured from where the user is actually watching rather than from
    /// the prefetch frontier (how far the player has fetched segments ahead).
    /// Older clients that never call this keep getting the pre-fix,
    /// segment-request-derived estimate — see
    /// <see cref="NoMercy.Api.Services.LiveTranscodeService.GetSegmentAsync"/>.
    /// </summary>
    public HubCommandResult ReportPlayhead(string sessionId, double currentTimeSeconds)
    {
        if (
            !double.IsFinite(currentTimeSeconds)
            || currentTimeSeconds < 0
            || currentTimeSeconds > TimeSpan.MaxValue.TotalSeconds
        )
            return HubCommandResult.Invalid("Playhead must be a finite non-negative number.");

        HubCommandResult? error = GetRuntimeError(sessionId, out LiveRuntimeSession runtime);
        if (error is not null)
            return error;

        return HubCommandResult.Execute(
            () =>
            {
                runtime.Session.ReportPlaybackPosition(
                    TimeSpan.FromSeconds(currentTimeSeconds),
                    authoritative: true
                );
                runtime.TouchLastAccess();
            },
            logger
        );
    }

    /// <summary>
    /// The watching client reports its download-buffer depth (seconds of media
    /// it holds downloaded but not yet played) and its measured/estimated
    /// downlink in kbps. This is a NETWORK signal, distinct from the
    /// encoder-capacity <see cref="ILiveSession.BufferAhead"/> the server
    /// already tracks — it drives the buffer-adaptive sweep's network axis
    /// (emergency drop / bandwidth-fit drop / hysteresis-gated raise). Older
    /// clients that never call this keep getting today's encoder-lead-only
    /// adaptive behavior — see
    /// <see cref="NoMercy.Encoder.LiveTranscode.BufferAdaptiveService"/>.
    /// </summary>
    public HubCommandResult ReportBufferHealth(
        string sessionId,
        double bufferedSeconds,
        double observedBandwidthKbps
    )
    {
        if (
            !double.IsFinite(bufferedSeconds)
            || bufferedSeconds < 0
            || bufferedSeconds > TimeSpan.MaxValue.TotalSeconds
            || !double.IsFinite(observedBandwidthKbps)
            || observedBandwidthKbps < 0
            || observedBandwidthKbps > int.MaxValue
        )
            return HubCommandResult.Invalid("Buffer health values are out of range.");

        HubCommandResult? error = GetRuntimeError(sessionId, out LiveRuntimeSession runtime);
        if (error is not null)
            return error;

        return HubCommandResult.Execute(
            () =>
            {
                runtime.Session.ReportClientBufferHealth(
                    TimeSpan.FromSeconds(bufferedSeconds),
                    (int)observedBandwidthKbps
                );
                runtime.TouchLastAccess();
            },
            logger
        );
    }

    /// <summary>
    /// Client requests the encoder pause (fill buffer to max, stop producing
    /// new segments). Maps to <see cref="ILiveSession.Suspend"/>.
    /// </summary>
    public HubCommandResult RequestPause(string sessionId)
    {
        HubCommandResult? error = GetRuntimeError(sessionId, out LiveRuntimeSession runtime);
        if (error is not null)
            return error;

        return HubCommandResult.Execute(runtime.Session.Suspend, logger);
    }

    /// <summary>
    /// Client requests the encoder resume after a pause.
    /// Maps to <see cref="ILiveSession.Resume"/>.
    /// </summary>
    public HubCommandResult RequestResume(string sessionId)
    {
        HubCommandResult? error = GetRuntimeError(sessionId, out LiveRuntimeSession runtime);
        if (error is not null)
            return error;

        return HubCommandResult.Execute(runtime.Session.Resume, logger);
    }

    private HubCommandResult? GetRuntimeError(string sessionId, out LiveRuntimeSession runtime)
    {
        runtime = null!;
        if (string.IsNullOrWhiteSpace(sessionId))
            return HubCommandResult.Invalid("Session id is required.");
        if (!streamingService.TryGetRuntime(sessionId, out runtime))
            return HubCommandResult.NotFound("Session was not found.");
        return ValidateSession(sessionId);
    }

    private HubCommandResult? ValidateSession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return HubCommandResult.Invalid("Session id is required.");
        if (string.IsNullOrWhiteSpace(sessionManager.GetOwnerUserId(sessionId)))
            return HubCommandResult.NotFound("Session was not found.");
        if (!CallerOwnsSession(sessionId))
            return HubCommandResult.Forbidden("Session does not belong to the caller.");
        return null;
    }

    private bool CallerOwnsSession(string sessionId)
    {
        string? ownerId = sessionManager.GetOwnerUserId(sessionId);
        string callerId = Context.UserIdentifier ?? string.Empty;
        return string.Equals(ownerId, callerId, StringComparison.OrdinalIgnoreCase);
    }
}
