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

using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugins.Runtime;

/// <summary>What the host decided to run, after every check has passed.</summary>
public sealed record PluginProcessRequest(
    string Binary,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string>? Environment,
    string? WorkingDirectory
);

/// <summary>
/// Starting the child, kept behind an interface so the guards around it can be
/// tested without spawning anything: what matters is WHICH binary is reached
/// and with what, and a real process proves neither.
/// </summary>
public interface IPluginProcessStarter
{
    IPluginProcessHandle Start(PluginProcessRequest request);
}
