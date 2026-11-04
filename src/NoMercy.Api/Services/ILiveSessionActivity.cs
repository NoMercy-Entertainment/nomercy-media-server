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

using NoMercy.Database.Models.Media;
using NoMercy.Encoder.LiveTranscode;

namespace NoMercy.Api.Services;

/// <summary>
/// Writes the activity-log entries that say who started a live transcode, on
/// which device, and when it ended. Never throws: a failed write must not fail
/// the session.
/// </summary>
public interface ILiveSessionActivity : ILiveSessionEndObserver
{
    Task RecordStartedAsync(
        string sessionId,
        Guid userId,
        string? deviceId,
        VideoFile file,
        LiveQuality quality,
        double startSeconds
    );
}
