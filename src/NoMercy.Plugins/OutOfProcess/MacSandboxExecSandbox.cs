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
using System.Runtime.Versioning;
using NoMercy.Plugins.Quotas;

namespace NoMercy.Plugins.OutOfProcess;

/// <summary>
/// A plugin process under a macOS sandbox profile.
/// <para>
/// The profile has to be in place before the plugin's first instruction, so
/// this wraps the launch rather than confining a process that is already
/// running. macOS offers nothing that confines a running process the way a job
/// object or a cgroup does.
/// </para>
/// <para>
/// A descendant inherits the profile, so a binary the plugin starts through
/// <c>process.spawn</c> is held by the same rules as the plugin, with nothing
/// extra to configure.
/// </para>
/// <para>
/// There is no hard memory ceiling here. macOS has no per-process equivalent
/// of a job object's memory limit or a cgroup's <c>memory.max</c>, so on this
/// platform the memory quota stays what the supervisor samples. Saying that is
/// the point: an owner told their plugin was capped, on the one platform where
/// it is not, would find out when the machine swapped.
/// </para>
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacSandboxExecSandbox : IPluginSandbox, IDisposable
{
    private const string SandboxExec = "/usr/bin/sandbox-exec";

    private readonly PluginSandboxGrants _grants;
    private string? _profilePath;
    private bool _wrapped;

    public MacSandboxExecSandbox(PluginSandboxGrants grants)
    {
        _grants = grants;
    }

    public bool Available => File.Exists(SandboxExec);

    public PluginSandboxLaunch Wrap(PluginSandboxLaunch launch)
    {
        if (!Available)
            return launch;

        _profilePath = WriteProfile(_grants, launch.FileName);

        if (_profilePath is null)
            return launch;

        _wrapped = true;

        return new PluginSandboxLaunch(
            SandboxExec,
            ["-f", _profilePath!, launch.FileName, .. launch.Arguments]
        );
    }

    /// <summary>
    /// True only when this sandbox actually wrapped the launch. macOS cannot
    /// confine a process after it started, so answering true for one that was
    /// started around this sandbox would report confinement nobody applied.
    /// </summary>
    public bool Confine(Process process, PluginQuota quota) => Available && _wrapped;

    private static string? WriteProfile(PluginSandboxGrants grants, string executable)
    {
        try
        {
            string path = Path.Combine(Path.GetTempPath(), $"nomercy-plugin-{Guid.NewGuid():n}.sb");

            File.WriteAllText(
                path,
                MacSandboxProfile.For(grants.DataFolder, grants.AllowsSpawn, executable)
            );

            return path;
        }
        catch (Exception)
        {
            // A profile that could not be written is a sandbox that cannot
            // hold anything, and saying so is the point: a stub reporting
            // success would put a plugin into what the rest of the server
            // believes is a sandbox.
            return null;
        }
    }

    public void Dispose()
    {
        if (_profilePath is null)
            return;

        try
        {
            File.Delete(_profilePath);
        }
        catch (Exception)
        {
            // A leftover profile in the temp folder is not worth failing a
            // shutdown over.
        }
    }
}
