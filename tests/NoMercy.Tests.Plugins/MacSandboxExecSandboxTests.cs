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

using System.Runtime.Versioning;
using FluentAssertions;
using NoMercy.Plugins.OutOfProcess;
using NoMercy.Plugins.Quotas;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// The macOS sandbox wraps the launch rather than confining a process that is
/// already running, because macOS offers nothing that confines a running
/// process the way a job object or a cgroup does.
/// </summary>
[Trait("Category", "Integration")]
public class MacSandboxExecSandboxTests
{
    private static bool OnMac => OperatingSystem.IsMacOS();

    [SkippableFact]
    [SupportedOSPlatform("macos")]
    public void TheLaunchIsRewrittenToRunUnderAProfile()
    {
        Skip.IfNot(OnMac, "sandbox-exec is a macOS facility.");

        using MacSandboxExecSandbox sandbox = new(Grants());

        PluginSandboxLaunch wrapped = sandbox.Wrap(new PluginSandboxLaunch("/host", ["--dev"]));

        wrapped.FileName.Should().Be("/usr/bin/sandbox-exec");
        wrapped.Arguments.Should().ContainInOrder("-f");
        wrapped.Arguments.Should().ContainInOrder("/host", "--dev");
    }

    /// <summary>
    /// The profile has to name the binary it is about to start, so it cannot
    /// be written before that binary is known.
    /// </summary>
    [SkippableFact]
    [SupportedOSPlatform("macos")]
    public void TheProfileNamesTheBinaryTheLaunchAsksFor()
    {
        Skip.IfNot(OnMac, "sandbox-exec is a macOS facility.");

        using MacSandboxExecSandbox sandbox = new(Grants());

        PluginSandboxLaunch wrapped = sandbox.Wrap(new PluginSandboxLaunch("/host", []));

        string profilePath = wrapped.Arguments[1];

        File.ReadAllText(profilePath).Should().Contain("(allow process-exec (literal \"/host\"))");
    }

    /// <summary>
    /// macOS cannot confine a process after it started, so answering true for
    /// one that was started around this sandbox would report confinement
    /// nobody applied.
    /// </summary>
    [SkippableFact]
    [SupportedOSPlatform("macos")]
    public void ALaunchThatWasNeverWrapped_IsNotReportedAsConfined()
    {
        Skip.IfNot(OnMac, "sandbox-exec is a macOS facility.");

        using MacSandboxExecSandbox sandbox = new(Grants());

        sandbox.Confine(System.Diagnostics.Process.GetCurrentProcess(), Quota()).Should().BeFalse();
    }

    private static PluginSandboxGrants Grants() =>
        new(Path.Combine(Path.GetTempPath(), "nomercy-plugin-test"), AllowsSpawn: false);

    private static PluginQuota Quota() => new(25, 256L * 1024 * 1024, 0, 0);
}
