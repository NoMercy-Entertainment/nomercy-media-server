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
using NoMercy.Database.Activity;
using NoMercy.Database.Models.Users;
using NoMercy.Events;
using NoMercy.Events.Encoding;
using NoMercy.Events.FileWatcher;
using NoMercy.Events.Library;

namespace NoMercy.Data.EventHandlers;

/// <summary>
/// Writes the server's own work into the activity log.
/// </summary>
/// <remarks>
/// The log used to hold only things a person did — signing in, connecting, pressing play — so
/// it could describe the clients and never the server. Everything that actually takes time here
/// is automatic: encodes, scans, and files the watcher notices, and none of it left a trace.
///
/// This listens on the event bus rather than reaching into the encoder or the scanner. Those
/// already announce what they are doing because the dashboard needs live progress; recording it
/// is a second subscriber, not a second call site, so no job had to learn what an activity log
/// is.
/// </remarks>
public class ActivityEventHandler : EventSubscriber
{
    private readonly IActivityLogger _activityLogger;
    private readonly ILogger<ActivityEventHandler> _logger;

    public ActivityEventHandler(
        ILogger<ActivityEventHandler> logger,
        IEventBus eventBus,
        IActivityLogger activityLogger
    )
    {
        _logger = logger;
        _activityLogger = activityLogger;

        Track(eventBus.Subscribe<EncodingStartedEvent>(OnEncodingStarted));
        Track(eventBus.Subscribe<EncodingCompletedEvent>(OnEncodingCompleted));
        Track(eventBus.Subscribe<EncodingFailedEvent>(OnEncodingFailed));
        Track(eventBus.Subscribe<LibraryScanStartedEvent>(OnScanStarted));
        Track(eventBus.Subscribe<LibraryScanCompletedEvent>(OnScanCompleted));
        Track(eventBus.Subscribe<FileCreatedEvent>(OnFileCreated));
    }

    internal Task OnEncodingStarted(EncodingStartedEvent @event, CancellationToken ct) =>
        WriteAsync(
            ActivityCategory.Encoder,
            "encoder.started",
            new
            {
                job_id = @event.JobId,
                profile = @event.ProfileName,
                input = FileNameOf(@event.InputPath),
            },
            ct
        );

    internal Task OnEncodingCompleted(EncodingCompletedEvent @event, CancellationToken ct) =>
        WriteAsync(
            ActivityCategory.Encoder,
            "encoder.completed",
            new
            {
                job_id = @event.JobId,
                output = FileNameOf(@event.OutputPath),
                duration_seconds = (int)@event.Duration.TotalSeconds,
            },
            ct
        );

    internal Task OnEncodingFailed(EncodingFailedEvent @event, CancellationToken ct) =>
        WriteAsync(
            ActivityCategory.Failure,
            "encoder.failed",
            new
            {
                job_id = @event.JobId,
                input = FileNameOf(@event.InputPath),
                message = @event.ErrorMessage,
            },
            ct,
            success: false,
            errorCode: @event.ExceptionType
        );

    internal Task OnScanStarted(LibraryScanStartedEvent @event, CancellationToken ct) =>
        WriteAsync(
            ActivityCategory.Library,
            "library.scan_started",
            new { library = @event.LibraryName },
            ct
        );

    internal Task OnScanCompleted(LibraryScanCompletedEvent @event, CancellationToken ct) =>
        WriteAsync(
            ActivityCategory.Library,
            "library.scan_completed",
            new
            {
                library = @event.LibraryName,
                items = @event.ItemsFound,
                duration_seconds = (int)@event.Duration.TotalSeconds,
            },
            ct
        );

    /// <summary>
    /// A file the watcher noticed on its own, as opposed to one an operator picked in the
    /// dashboard. They are separate event types rather than one with a flag because the
    /// question the log gets asked is "did I do this, or did the server?".
    /// </summary>
    internal Task OnFileCreated(FileCreatedEvent @event, CancellationToken ct) =>
        WriteAsync(
            ActivityCategory.Library,
            "library.content_added_automatically",
            new { folder = FileNameOf(@event.FolderPath), type = @event.LibraryType },
            ct
        );

    private async Task WriteAsync(
        ActivityCategory category,
        string type,
        object metadata,
        CancellationToken ct,
        bool success = true,
        string? errorCode = null
    )
    {
        try
        {
            await _activityLogger.LogSystemAsync(
                category,
                type,
                success: success,
                errorCode: errorCode,
                metadata: metadata,
                ct: ct
            );
        }
        catch (Exception ex)
        {
            // Recording that something happened must never be the reason it fails.
            _logger.LogWarning("Could not record activity {Type}: {Message}", [type, ex.Message]);
        }
    }

    /// <summary>
    /// Paths are long and the interesting part is at the end; the log shows the name, and the
    /// full path stays in the encoder's own logs where a path is what you want.
    /// </summary>
    private static string FileNameOf(string path) =>
        string.IsNullOrWhiteSpace(path)
            ? string.Empty
            : path.Replace('\\', '/').TrimEnd('/').Split('/').Last();
}
