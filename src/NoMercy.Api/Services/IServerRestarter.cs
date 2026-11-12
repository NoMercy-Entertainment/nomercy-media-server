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

namespace NoMercy.Api.Services;

public interface IServerRestarter
{
    /// <summary>
    /// Secures the way back: picks the supervisor, or starts the replacement process.
    /// The server keeps running; an outcome with an error means nothing will restart.
    /// </summary>
    ServerRestartOutcome Begin();

    /// <summary>
    /// Stops this server so the way back from <see cref="Begin"/> takes over.
    /// Does nothing for an outcome with an error.
    /// </summary>
    void Complete(ServerRestartOutcome outcome);
}
