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

using System.Text.Json;
using FluentAssertions;
using NoMercy.PluginSdk.Abstractions;
using Xunit;

namespace NoMercy.Tests.Plugins;

public class PluginIdTests
{
    private const string Sample = "01J9ZK5V8Y0000000000000000";

    [Fact]
    public void Parse_RoundTripsThroughToString()
    {
        PluginId id = PluginId.Parse(Sample);

        id.ToString().Should().Be(Sample);
    }

    [Fact]
    public void TryParse_RefusesRubbish()
    {
        bool parsed = PluginId.TryParse("not-an-id", out PluginId id);

        parsed.Should().BeFalse();
        id.Should().Be(PluginId.Empty);
    }

    [Fact]
    public void Serialization_WritesTheBareString()
    {
        string json = JsonSerializer.Serialize(PluginId.Parse(Sample));

        json.Should().Be($"\"{Sample}\"");
    }

    [Fact]
    public void Deserialization_ReadsTheBareString()
    {
        PluginId id = JsonSerializer.Deserialize<PluginId>($"\"{Sample}\"");

        id.Should().Be(PluginId.Parse(Sample));
    }

    [Fact]
    public void TwoKindsOfIdAreNotTheSameType()
    {
        typeof(PluginId).Should().NotBe(typeof(LibraryId));
    }
}
