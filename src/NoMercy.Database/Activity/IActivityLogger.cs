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

namespace NoMercy.Database.Activity;

public interface IActivityLogger
{
    Task LogAuthAsync(
        string type,
        Guid userId,
        Ulid deviceId,
        bool success,
        string? errorCode = null,
        object? metadata = null,
        CancellationToken ct = default
    );

    Task LogConnectionAsync(
        string type,
        Guid userId,
        Ulid deviceId,
        CancellationToken ct = default
    );

    Task LogPlaybackAsync(
        string type,
        Guid userId,
        Ulid deviceId,
        Ulid mediaId,
        object? metadata = null,
        CancellationToken ct = default
    );

    Task LogConfigurationAsync(
        string type,
        Guid userId,
        Ulid deviceId,
        string configKey,
        object? oldValue,
        object? newValue,
        CancellationToken ct = default
    );

    Task LogFailureAsync(
        string type,
        Guid userId,
        Ulid deviceId,
        string errorCode,
        string message,
        Ulid? mediaId = null,
        object? metadata = null,
        CancellationToken ct = default
    );

    /// <summary>
    /// Records something the server did on its own — an encode, a scheduled scan, a file the
    /// watcher picked up.
    /// </summary>
    /// <remarks>
    /// There is deliberately no user or device here. Attributing an automatic import to
    /// whoever happened to be signed in would be a lie, and attributing it to a placeholder
    /// account would make the log unreadable. These rows say what happened and leave who
    /// blank, which is the truth.
    /// </remarks>
    Task LogSystemAsync(
        ActivityCategory category,
        string type,
        Ulid? mediaId = null,
        bool success = true,
        string? errorCode = null,
        object? metadata = null,
        CancellationToken ct = default
    );
}
