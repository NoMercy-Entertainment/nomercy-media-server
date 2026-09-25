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

namespace NoMercy.Notifications.Push;

/// <summary>
/// NoMercy.Notifications has no reference to NoMercy.Database, so this contract
/// is the seam: the implementation (which does) lives in a project that can
/// query VideoFile rows, and is injected here.
/// </summary>
public interface IPlayableMediaProbe
{
    /// <summary>
    /// True once the movie or show identified by <paramref name="mediaType"/> /
    /// <paramref name="mediaId"/> has at least one playable video file.
    /// </summary>
    Task<bool> HasPlayableVideoAsync(string mediaType, int mediaId, CancellationToken ct = default);
}
