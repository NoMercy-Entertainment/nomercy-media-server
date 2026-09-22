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

using System.Diagnostics;
using NoMercy.Plugins.Quotas;

namespace NoMercy.Plugins.OutOfProcess;

/// <summary>
/// What the operating system is asked to enforce about a plugin process.
/// <para>
/// A quota the supervisor checks is a quota a plugin can exceed between two
/// checks. The kernel is the only thing that can refuse an allocation at the
/// moment it is made, so the numbers are handed to it as well.
/// </para>
/// </summary>
public static class PluginSandboxLimits
{
    /// <summary>
    /// The plugin's own process plus fifteen children.
    /// <para>
    /// A cap rather than none: a plugin that spawns in a loop is the cheapest
    /// way to take a machine down, and the count is the only thing the kernel
    /// can refuse before the machine is already unusable.
    /// </para>
    /// </summary>
    public const uint ProcessesPerPlugin = 16;
}

public interface IPluginSandbox
{
    /// <summary>Whether this sandbox can do anything on the machine it is running on.</summary>
    bool Available { get; }

    /// <summary>
    /// Confines a process that is already running. Returns false when the
    /// confinement could not be applied, which the caller must treat as a
    /// reason not to run the plugin rather than a warning to log.
    /// </summary>
    bool Confine(Process process, PluginQuota quota);
}

/// <summary>
/// The sandbox for a platform whose real one is not written yet.
/// <para>
/// It reports that it is unavailable rather than pretending to confine. A
/// stub that returned success would put a plugin into what the rest of the
/// server believes is a sandbox, and the first time one misbehaved the owner
/// would find out for us.
/// </para>
/// </summary>
public sealed class NullPluginSandbox : IPluginSandbox
{
    public bool Available => false;

    public bool Confine(Process process, PluginQuota quota) => false;
}
