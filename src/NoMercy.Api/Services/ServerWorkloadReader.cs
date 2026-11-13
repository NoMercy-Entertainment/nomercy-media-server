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

using NoMercy.Api.DTOs.Management;
using NoMercy.Encoder.LiveTranscode;
using NoMercyQueue;

namespace NoMercy.Api.Services;

public sealed class ServerWorkloadReader(ISessionManager sessionManager, QueueRunner queueRunner)
    : IServerWorkloadReader
{
    public ManagementActivityDto Read() =>
        From(sessionManager.ActiveSessionCount, queueRunner.GetActiveWorkerThreads().Keys);

    internal static ManagementActivityDto From(
        int activeStreams,
        IEnumerable<string> workerThreadNames
    )
    {
        int activeEncodes = workerThreadNames.Count(name =>
            name.StartsWith("encoder", StringComparison.OrdinalIgnoreCase)
        );

        // All encode jobs in V3 are split/resumable, so killing mid-encode is safe.
        // Streams are never "safe to interrupt" — stopping one ends playback for that user.
        return new()
        {
            ActiveStreams = activeStreams,
            ActiveEncodes = activeEncodes,
            CanInterruptSafely = activeStreams == 0,
        };
    }
}
