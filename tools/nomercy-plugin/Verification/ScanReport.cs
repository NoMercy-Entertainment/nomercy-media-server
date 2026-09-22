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

using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.Plugin.Cli;

/// <summary>
/// What a scan found, and whether it is fatal.
/// <para>
/// Exit code is derived rather than set, so a finding cannot be added without
/// deciding what it costs. Blocked fails the build; a warning prints and
/// still exits zero, because a gate that fails on advice is a gate people
/// switch off.
/// </para>
/// </summary>
public sealed record ScanReport
{
    public required IReadOnlyList<PluginRefusal> Refusals { get; init; }

    public int ExitCode =>
        Refusals.Any(refusal => refusal.Severity == PluginRefusalSeverity.Blocked) ? 1 : 0;

    public bool HasWarnings =>
        Refusals.Any(refusal => refusal.Severity != PluginRefusalSeverity.Blocked);
}
