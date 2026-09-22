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

namespace NoMercy.Plugins.OutOfProcess;

/// <summary>
/// One sandbox per plugin, of whatever kind this machine can enforce.
/// <para>
/// A sandbox holds one plugin's process and dies with it, so this hands out a
/// new one per plugin rather than a shared singleton: a shared job object or
/// cgroup would apply one memory ceiling to every plugin at once, and closing
/// it for one plugin would kill the others.
/// </para>
/// <para>
/// A platform whose sandbox is not written yet gets one that reports itself
/// unavailable. The caller treats that as a reason to say what the owner is
/// and is not getting, never as a warning to log and carry on.
/// </para>
/// </summary>
public interface IPluginSandboxFactory
{
    IPluginSandbox Create();
}

public sealed class PluginSandboxFactory : IPluginSandboxFactory
{
    public IPluginSandbox Create()
    {
        if (OperatingSystem.IsWindows())
            return new WindowsJobObjectSandbox();

        if (OperatingSystem.IsLinux())
            return new LinuxCgroupSandbox();

        return new NullPluginSandbox();
    }
}
