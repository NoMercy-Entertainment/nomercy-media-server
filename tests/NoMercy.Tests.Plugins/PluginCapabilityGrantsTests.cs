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
using Moq;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Capabilities;
using NoMercy.PluginSdk.OutOfProcess;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// The sandbox of an out-of-process plugin allows a spawn only when the
/// plugin holds process.spawn. The owner's approved binaries are stored
/// under the capability grant kind, so that is where the check must look.
/// </summary>
[Trait("Category", "Unit")]
public class PluginCapabilityGrantsTests
{
    private static readonly Ulid Plugin = Ulid.Parse("01J9ZK5V8Y000000000000000F");

    private static PluginCapabilityGrants With(string kind, params string[] values)
    {
        Mock<IPluginGrantStore> store = new();
        store.Setup(one => one.Granted(It.IsAny<Ulid>(), It.IsAny<string>())).Returns([]);
        store.Setup(one => one.Granted(Plugin, kind)).Returns(values);
        return new(store.Object);
    }

    [Fact]
    public void An_approved_binary_lets_the_sandbox_allow_a_spawn()
    {
        PluginCapabilityGrants grants = With(
            PluginGrantKind.ForCapability(PluginCapabilityNames.ProcessSpawn),
            "C:/ffmpeg/bin/ffmpeg.exe"
        );

        grants.Holds(Plugin, PluginCapabilityNames.ProcessSpawn).Should().BeTrue();
    }

    [Fact]
    public void A_grant_stored_under_the_bare_name_still_counts()
    {
        PluginCapabilityGrants grants = With(PluginCapabilityNames.ProcessSpawn, "*");

        grants.Holds(Plugin, PluginCapabilityNames.ProcessSpawn).Should().BeTrue();
    }

    [Fact]
    public void Without_a_grant_the_sandbox_allows_no_spawn()
    {
        PluginCapabilityGrants grants = With("network.host", "*");

        grants.Holds(Plugin, PluginCapabilityNames.ProcessSpawn).Should().BeFalse();
    }
}
