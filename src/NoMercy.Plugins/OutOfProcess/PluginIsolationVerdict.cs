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

namespace NoMercy.PluginSdk.OutOfProcess;

/// <summary>
/// Whether a plugin can run in a process of its own, and if not, what it uses
/// that the out-of-process runtime does not carry.
/// <para>
/// Read from the manifest, so it names only what a manifest declares. A plugin
/// can also use what no hook declares - reading music, playback, the browser,
/// or answering searches (<see cref="PluginRemoteReach.HooklessEntryPoints" />)
/// - and none of those cross either. A verdict that it can run out of process
/// therefore means nothing it declares is lost, not that nothing is.
/// </para>
/// </summary>
/// <param name="Facades">
/// <see cref="NoMercy.PluginSdk.Abstractions.IPluginContext" /> members its
/// manifest says it uses and the other process leaves null.
/// </param>
/// <param name="Hooks">Entry-point hooks it declares that the server would never call there.</param>
/// <param name="Controllers">Whether it serves REST controllers, which get no route there.</param>
public sealed record PluginIsolationVerdict(
    IReadOnlyList<string> Facades,
    IReadOnlyList<string> Hooks,
    bool Controllers
)
{
    public bool CanRunOutOfProcess => Facades.Count == 0 && Hooks.Count == 0 && !Controllers;
}
