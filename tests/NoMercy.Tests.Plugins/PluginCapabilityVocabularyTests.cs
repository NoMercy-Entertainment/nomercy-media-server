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
using NoMercy.PluginSdk.Abstractions;
using Xunit;

namespace NoMercy.Tests.Plugins;

public class PluginCapabilityVocabularyTests
{
    [Fact]
    public void All_CarriesEveryCapability()
    {
        PluginCapabilityVocabulary.All.Should().HaveCount(54);
    }

    [Fact]
    public void ByName_FindsACapabilityAndItsTrustFloor()
    {
        PluginCapabilityDescriptor? spawn = PluginCapabilityVocabulary.ByName(
            PluginCapabilityNames.ProcessSpawn
        );

        spawn.Should().NotBeNull();
        spawn!.Trust.Should().Be(PluginTrust.High);
        spawn.Reversible.Should().BeFalse();
    }

    [Fact]
    public void ByName_AnswersNullForAWordThatIsNotACapability()
    {
        PluginCapabilityVocabulary.ByName("network.everything").Should().BeNull();
    }

    [Fact]
    public void BrowserHeadless_IsMediumAndReversible()
    {
        PluginCapabilityDescriptor browser = PluginCapabilityVocabulary.ByName(
            PluginCapabilityNames.BrowserHeadless
        )!;

        browser.Trust.Should().Be(PluginTrust.Medium);
        browser.Reversible.Should().BeTrue();
    }
}
