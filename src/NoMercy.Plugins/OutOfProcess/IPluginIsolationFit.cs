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

namespace NoMercy.PluginSdk.OutOfProcess;

/// <summary>
/// Asks whether a plugin can run in a process of its own, from its manifest.
/// <para>
/// The manifest is what the server knows before any of a plugin's code is
/// mapped, which is when the isolation question is asked. It names the hooks a
/// plugin declares and whether it serves REST; it does not name facades or
/// entry points that need no hook, such as reading music or answering a
/// search, so those are not part of the answer.
/// </para>
/// </summary>
public interface IPluginIsolationFit
{
    PluginIsolationVerdict For(PluginCapabilities? capabilities);
}
