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

using NoMercy.MediaProcessing.Jobs.MediaJobs;
using NoMercyQueue.Core.Models;

namespace NoMercy.Api.Controllers.V1.Dashboard.Admin;

/// <summary>
/// A queue row paired with the music encode read from its payload. The row owns
/// the scheduling state; the payload names the release and the track.
/// </summary>
internal sealed record MusicQueueRow(QueueJobModel Row, MusicEncodeJob Job);
