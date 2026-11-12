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

/// <summary>
/// How a requested restart brings the server back, or why it cannot.
/// </summary>
public sealed record ServerRestartOutcome(string Mode, string? Error)
{
    public const string Supervisor = "supervisor";
    public const string Relaunch = "relaunch";
}
