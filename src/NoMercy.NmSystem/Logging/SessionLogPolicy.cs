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
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace NoMercy.NmSystem.Logging;

/// <summary>
/// Which session calls are repeated reports and so log at Debug, and how often a state
/// broadcast may log at Information. The level of a whole category is a separate setting
/// (<see cref="NoMercyLoggerOptions.ApplyEnvironment" />).
/// </summary>
public sealed class SessionLogPolicy
{
    public const string QuietMethodsVariable = "NOMERCY_LOG_SESSION_QUIET_METHODS";
    public const string StateIntervalVariable = "NOMERCY_LOG_SESSION_STATE_INTERVAL_SECONDS";

    public static readonly IReadOnlyList<string> DefaultQuietMethods =
    [
        "SetTime",
        "CurrentTimeCommand",
        "CurrentTimeForItemCommand",
        "ReportPositionCommand",
        "ReportPositionAtCommand",
        "ReportPositionForItemCommand",
        "GetServerTime",
        "Time",
        "Play",
        "Pause",
        "Ended",
        "Volume",
        "Muted",
        "Item",
        "Playlist",
        "SubtitleTracks",
        "CurrentSubtitleTrack",
        "AudioTracks",
        "CurrentAudioTrack",
        "GetPlayerState",
        "PlayerState",
        "SetAudioTrack",
        "SetSubtitleTrack",
        "SetPlaylistItem",
        "SetVolume",
        "SetMuted",
        "SetSeek",
        "SetNext",
        "SetPrevious",
        "SetPlay",
        "SetPause",
        "SetStop",
    ];

    private static readonly HashSet<string> StateBroadcasts = new(StringComparer.Ordinal)
    {
        "ChangeDevice",
        "MusicPlayerState",
        "VideoPlayerState",
        "ConnectedDevicesState",
        "DeviceListChanged",
    };

    private readonly HashSet<string> _quietMethods;
    private readonly long _stateIntervalMs;
    private readonly ConcurrentDictionary<(Guid User, string Event), long> _lastStateLog = new();

    public SessionLogPolicy(
        IEnumerable<string>? quietMethods = null,
        TimeSpan? stateInterval = null
    )
    {
        _quietMethods = new(quietMethods ?? DefaultQuietMethods, StringComparer.Ordinal);
        _stateIntervalMs = (long)(stateInterval ?? TimeSpan.FromSeconds(10)).TotalMilliseconds;
    }

    public static SessionLogPolicy FromEnvironment(Func<string, string?> getVariable)
    {
        string? methods = getVariable(QuietMethodsVariable);
        List<string>? quiet = string.IsNullOrWhiteSpace(methods)
            ? null
            :
            [
                .. methods.Split(
                    ',',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
                ),
            ];

        TimeSpan? interval = null;
        if (
            double.TryParse(
                getVariable(StateIntervalVariable),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double seconds
            )
            && seconds >= 0
        )
            interval = TimeSpan.FromSeconds(seconds);

        return new(quiet, interval);
    }

    /// <summary>Information for a user request, Debug for a repeated report.</summary>
    public LogLevel RequestLevel(string method) =>
        _quietMethods.Contains(method) ? LogLevel.Debug : LogLevel.Information;

    /// <summary>
    /// Information for the first broadcast of a state event to a user in each interval,
    /// Debug for the rest, so a position-driven state push never floods the log.
    /// </summary>
    public LogLevel BroadcastLevel(string eventName, Guid userId)
    {
        if (!StateBroadcasts.Contains(eventName))
            return LogLevel.Debug;

        long now = Environment.TickCount64;
        (Guid, string) key = (userId, eventName);
        bool first =
            !_lastStateLog.TryGetValue(key, out long last) || now - last >= _stateIntervalMs;
        if (first)
            _lastStateLog[key] = now;

        return first ? LogLevel.Information : LogLevel.Debug;
    }
}
