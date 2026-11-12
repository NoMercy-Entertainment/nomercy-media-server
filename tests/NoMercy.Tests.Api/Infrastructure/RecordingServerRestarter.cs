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

using NoMercy.Api.Services;

namespace NoMercy.Tests.Api.Infrastructure;

/// <summary>
/// Stands in for the real restarter so no test spawns a process or stops the test host.
/// </summary>
public sealed class RecordingServerRestarter : IServerRestarter
{
    private readonly TaskCompletionSource<ServerRestartOutcome> _completed = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );

    public ServerRestartOutcome Outcome { get; init; } = new(ServerRestartOutcome.Relaunch, null);

    public Task<ServerRestartOutcome> Completed => _completed.Task;

    public ServerRestartOutcome Begin() => Outcome;

    public void Complete(ServerRestartOutcome outcome) => _completed.TrySetResult(outcome);
}
