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

using NoMercy.OpticalMedia.Sources;

namespace NoMercy.Api.Controllers.V1.Dashboard.Media;

/// <summary>
/// Request body for <c>POST /optical/{drivePath}/confirm</c>.
/// </summary>
/// <summary>
/// Request body for <c>POST /optical/{drivePath}/play/{playlistId}</c>. Reuses
/// the rip endpoint's <see cref="AudioTrackSelection"/> shape so the dashboard
/// client sends the same <c>{ StreamIndex, Include }</c> pairs it already
/// builds for <see cref="RipRequest.AudioTracks"/> — no parallel DTO. Omitted
/// or empty keeps the pre-existing single-default-track behaviour.
/// </summary>
public record PlayMediaRequest(AudioTrackSelection[]? AudioTracks = null);
