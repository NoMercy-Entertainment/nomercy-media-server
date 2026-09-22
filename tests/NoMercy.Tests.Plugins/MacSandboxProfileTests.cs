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
using NoMercy.PluginSdk.OutOfProcess;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// The macOS profile, checked on every machine that builds this rather than
/// only on a Mac.
/// <para>
/// A rule that named the wrong path, or was left out, is a plugin reading the
/// owner's whole disk on a kernel that reported no error.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public class MacSandboxProfileTests
{
    private const string DataFolder = "/Users/stoney/Library/NoMercy/plugins/radio";
    private const string Host = "/usr/local/share/nomercy/NoMercy.PluginHost";

    /// <summary>
    /// A profile that started from allow would grant every capability nobody
    /// thought to write a rule against, which is the opposite of what the
    /// owner consented to.
    /// </summary>
    [Fact]
    public void TheProfileDeniesEverythingBeforeItAllowsAnything()
    {
        string profile = MacSandboxProfile.For(DataFolder, allowsSpawn: false, Host);

        profile.Should().Contain("(deny default)");
        profile
            .IndexOf("(deny default)", StringComparison.Ordinal)
            .Should()
            .BeLessThan(profile.IndexOf("(allow file-read*", StringComparison.Ordinal));
    }

    [Fact]
    public void ThePluginsOwnFolderIsTheOnlyPlaceItMayWrite()
    {
        string profile = MacSandboxProfile.For(DataFolder, allowsSpawn: false, Host);

        profile.Should().Contain($"(allow file-write* (subpath \"{DataFolder}\")");
        profile.Should().NotContain("(allow file-write* (subpath \"/\")");
    }

    /// <summary>
    /// sandbox-exec applies the profile and THEN runs its target, so a profile
    /// that does not name the target is a plugin that never starts.
    /// </summary>
    [Fact]
    public void TheProfileNamesTheBinaryItIsAboutToStart()
    {
        MacSandboxProfile
            .For(DataFolder, allowsSpawn: false, Host)
            .Should()
            .Contain($"(allow process-exec (literal \"{Host}\"))");
    }

    /// <summary>
    /// A plugin without process.spawn cannot execute anything beyond itself,
    /// even if it finds a binary, so the grant and the profile say the same
    /// thing and the kernel is the one enforcing it.
    /// </summary>
    [Fact]
    public void APluginWithoutTheSpawnGrantMayExecuteNothingBeyondItself()
    {
        MacSandboxProfile
            .For(DataFolder, allowsSpawn: false, Host)
            .Should()
            .NotContain($"(allow process-exec (subpath \"{DataFolder}\")");
    }

    [Fact]
    public void APluginWithTheSpawnGrantMayExecuteOnlyInsideItsOwnFolder()
    {
        string profile = MacSandboxProfile.For(DataFolder, allowsSpawn: true, Host);

        profile.Should().Contain($"(allow process-exec (subpath \"{DataFolder}\")");
    }

    /// <summary>
    /// Without the loader, the shared cache and the system frameworks the
    /// plugin is not confined, it simply never runs.
    /// </summary>
    [Fact]
    public void TheLoaderAndTheSystemFrameworksStayReadable()
    {
        string profile = MacSandboxProfile.For(DataFolder, allowsSpawn: false, Host);

        profile.Should().Contain("(subpath \"/usr/lib\")");
        profile.Should().Contain("(subpath \"/System\")");
    }

    /// <summary>
    /// The kernel checks the resolved path. A rule naming the firmlinked one
    /// matches nothing, and a rule that matches nothing reads exactly like a
    /// rule that was never needed.
    /// </summary>
    [Fact]
    public void AFirmlinkedPathIsNamedBothWaysBecauseTheKernelChecksTheResolvedOne()
    {
        MacSandboxProfile
            .Locations("/var/folders/wf/abc/T/plugin")
            .Should()
            .BeEquivalentTo([
                "/var/folders/wf/abc/T/plugin",
                "/private/var/folders/wf/abc/T/plugin",
            ]);
    }

    [Fact]
    public void AResolvedPathIsAlsoNamedBothWays()
    {
        MacSandboxProfile
            .Locations("/private/var/folders/wf/abc/T/plugin")
            .Should()
            .BeEquivalentTo([
                "/private/var/folders/wf/abc/T/plugin",
                "/var/folders/wf/abc/T/plugin",
            ]);
    }

    [Fact]
    public void APathWithNoFirmlinkIsNamedOnce()
    {
        MacSandboxProfile.Locations(DataFolder).Should().BeEquivalentTo([DataFolder]);
    }
}
