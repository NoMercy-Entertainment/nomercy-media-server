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

using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NoMercy.Database;
using NoMercy.Database.Activity;
using NoMercy.Database.Models.Media;
using NoMercy.Encoder.LiveTranscode;

namespace NoMercy.Api.Services;

/// <inheritdoc />
public class LiveSessionActivityRecorder(
    IActivityLogger activityLogger,
    IDbContextFactory<MediaContext> contextFactory,
    ILogger<LiveSessionActivityRecorder> logger
) : ILiveSessionActivity
{
    private const string StartedType = "playback.transcode_started";
    private const string EndedType = "playback.transcode_ended";

    private readonly ConcurrentDictionary<string, StartedSession> _started = new();

    public async Task RecordStartedAsync(
        string sessionId,
        Guid userId,
        string? deviceId,
        VideoFile file,
        LiveQuality quality,
        double startSeconds
    )
    {
        try
        {
            (Ulid device, string? deviceName, string? deviceType) = await ResolveDeviceAsync(
                userId,
                deviceId
            );

            Dictionary<string, object?> fields = new()
            {
                ["session_id"] = sessionId,
                ["media_type"] = "video",
                ["title"] = await ResolveTitleAsync(file),
                ["mode"] = "transcode",
                ["device_name"] = deviceName,
                ["device_type"] = deviceType,
                ["quality"] = quality.Label,
                ["width"] = quality.Width,
                ["height"] = quality.Height,
                ["bitrate_kbps"] = quality.BitrateKbps,
                ["codec"] = quality.Codec.ToString(),
                ["encoder"] = quality.Encoder,
                ["hardware_accelerated"] = quality.IsHardwareAccelerated,
                ["start_seconds"] = startSeconds,
            };

            _started[sessionId] = new(userId, device, file.Id, DateTime.UtcNow, fields);
            await activityLogger.LogPlaybackAsync(StartedType, userId, device, file.Id, fields);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not log transcode start for session {SessionId}", sessionId);
        }
    }

    public async Task OnSessionEndedAsync(string sessionId)
    {
        if (!_started.TryRemove(sessionId, out StartedSession? started))
            return;

        try
        {
            Dictionary<string, object?> fields = new(started.Fields)
            {
                ["duration_seconds"] = Math.Round(
                    (DateTime.UtcNow - started.StartedAt).TotalSeconds,
                    1
                ),
            };
            await activityLogger.LogPlaybackAsync(
                EndedType,
                started.UserId,
                started.DeviceId,
                started.MediaId,
                fields
            );
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not log transcode end for session {SessionId}", sessionId);
        }
    }

    // The same Device row the connect entry points at; an unknown device gives
    // Ulid.Empty, which the activity logger stores as no device.
    private async Task<(Ulid Id, string? Name, string? Type)> ResolveDeviceAsync(
        Guid userId,
        string? deviceId
    )
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return (Ulid.Empty, null, null);

        await using MediaContext ctx = await contextFactory.CreateDbContextAsync();
        var device = await ctx
            .Devices.AsNoTracking()
            .Where(d => d.DeviceId == deviceId && d.OwnerUserId == userId)
            .Select(d => new
            {
                d.Id,
                d.Name,
                d.CustomName,
                d.Type,
            })
            .FirstOrDefaultAsync();

        return device is null
            ? (Ulid.Empty, null, null)
            : (device.Id, device.CustomName ?? device.Name, device.Type);
    }

    private async Task<string> ResolveTitleAsync(VideoFile file)
    {
        await using MediaContext ctx = await contextFactory.CreateDbContextAsync();

        string? title = null;
        if (file.MovieId is int movieId)
        {
            title = await ctx
                .Movies.AsNoTracking()
                .Where(m => m.Id == movieId)
                .Select(m => m.Title)
                .FirstOrDefaultAsync();
        }
        else if (file.EpisodeId is int episodeId)
        {
            var episode = await ctx
                .Episodes.AsNoTracking()
                .Where(e => e.Id == episodeId)
                .Select(e => new
                {
                    Show = e.Tv.Title,
                    e.SeasonNumber,
                    e.EpisodeNumber,
                })
                .FirstOrDefaultAsync();
            if (episode is not null)
                title = $"{episode.Show} S{episode.SeasonNumber:00}E{episode.EpisodeNumber:00}";
        }

        return string.IsNullOrWhiteSpace(title)
            ? Path.GetFileNameWithoutExtension(file.Filename)
            : title;
    }

    private sealed record StartedSession(
        Guid UserId,
        Ulid DeviceId,
        Ulid MediaId,
        DateTime StartedAt,
        Dictionary<string, object?> Fields
    );
}
