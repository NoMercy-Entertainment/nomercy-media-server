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
/// A queue row paired with the job deserialized from its payload. The row is the
/// authority on scheduling state (id, priority, reservation); the payload only
/// describes the work that was requested.
/// </summary>
internal sealed record QueueJobEntry(QueueJobModel Row, VideoEncodeJob? Job);
