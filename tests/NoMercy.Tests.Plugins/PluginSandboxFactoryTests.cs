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

using FluentAssertions;
using NoMercy.Plugins.OutOfProcess;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// Which sandbox this machine gets.
/// <para>
/// A sandbox nothing chooses is a sandbox nobody uses, and the platform test
/// belongs in one place: written at each call site, a machine would end up
/// confined on one path and free on another.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public class PluginSandboxFactoryTests
{
    [Fact]
    public void TheMachineGetsTheSandboxItsOwnKernelCanEnforce()
    {
        IPluginSandbox sandbox = new PluginSandboxFactory().Create();

        if (OperatingSystem.IsWindows())
            sandbox.Should().BeOfType<WindowsJobObjectSandbox>();
        else if (OperatingSystem.IsLinux())
            sandbox.Should().BeOfType<LinuxCgroupSandbox>();
        else
            sandbox.Should().BeOfType<NullPluginSandbox>();

        (sandbox as IDisposable)?.Dispose();
    }

    /// <summary>
    /// A sandbox holds one plugin and dies with it. Shared, one memory ceiling
    /// would cover every plugin at once and closing it for one would kill the
    /// rest.
    /// </summary>
    [Fact]
    public void EachPluginGetsItsOwnSandboxRatherThanASharedOne()
    {
        PluginSandboxFactory factory = new();

        IPluginSandbox first = factory.Create();
        IPluginSandbox second = factory.Create();

        first.Should().NotBeSameAs(second);

        (first as IDisposable)?.Dispose();
        (second as IDisposable)?.Dispose();
    }

    /// <summary>
    /// A platform with no sandbox written says so rather than pretending. A
    /// stub that returned success would put a plugin into what the rest of the
    /// server believes is a sandbox.
    /// </summary>
    [Fact]
    public void APlatformWithoutASandbox_ReportsItselfUnavailable()
    {
        new NullPluginSandbox().Available.Should().BeFalse();
    }
}
