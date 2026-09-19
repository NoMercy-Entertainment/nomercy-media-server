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
using NoMercy.Plugins;
using NoMercy.Plugins.Abstractions;
using NoMercy.Plugins.Manifest;
using Xunit;

namespace NoMercy.Tests.Plugins;

public class PluginManifestV3Tests
{
    private const string V3 = """
    {
      "id": "5KTKRT4Z2Y9P59Y40W5CX4TQKF",
      "name": "Internet Radio",
      "description": "Radio stations from radio-browser.",
      "version": "2.0.0-beta.1",
      "targetAbi": "11.0",
      "publisher": "01J9ZK5V8Y0000000000000000",
      "tier": "free",
      "assembly": "NoMercy.Plugin.InternetRadio.dll",
      "entry": "NoMercy.Plugin.InternetRadio.InternetRadioPlugin",
      "license": "MIT",
      "capabilities": [
        { "name": "network.fetch", "scope": "*.api.radio-browser.info", "reason": "radio.reason.catalogue" },
        { "name": "media.proxy", "scope": "**", "reason": "radio.reason.streams" }
      ]
    }
    """;

    [Fact]
    public void Parses_every_new_field()
    {
        PluginManifestV3 manifest = PluginManifestParser.ParseV3(V3);

        manifest.Id.Should().Be(PluginId.Parse("5KTKRT4Z2Y9P59Y40W5CX4TQKF"));
        manifest.Publisher.Should().Be(PluginId.Parse("01J9ZK5V8Y0000000000000000"));
        manifest.Tier.Should().Be(PluginTier.Free);
        manifest.Entry.Should().Be("NoMercy.Plugin.InternetRadio.InternetRadioPlugin");
        manifest.License.Should().Be("MIT");
        manifest.Capabilities.Should().HaveCount(2);
        manifest.Capabilities[0].Reason.Should().Be("radio.reason.catalogue");
    }

    [Fact]
    public void Accepts_a_prerelease_version()
    {
        PluginManifestParser.ParseV3(V3).Version.Should().Be("2.0.0-beta.1");
    }

    [Fact]
    public void Refuses_a_capability_that_is_not_in_the_vocabulary()
    {
        string bad = V3.Replace("network.fetch", "network.everything");

        Action parse = () => PluginManifestParser.ParseV3(bad);

        parse.Should()
            .Throw<PluginRefusedException>()
            .Which.Refusal.Code.Should()
            .Be(PluginRefusalCodes.ManifestInvalid);
    }

    [Fact]
    public void Refuses_a_version_that_is_not_semver()
    {
        string bad = V3.Replace("\"2.0.0-beta.1\"", "\"2.0\"");

        Action parse = () => PluginManifestParser.ParseV3(bad);

        parse.Should()
            .Throw<PluginRefusedException>()
            .Which.Refusal.Fix.Should()
            .Contain("semver");
    }

    [Fact]
    public void Maps_every_v2_hook_to_its_capability()
    {
        PluginManifest v2 = new()
        {
            Id = Ulid.Parse("5KTKRT4Z2Y9P59Y40W5CX4TQKF"),
            Name = "Internet Radio",
            Description = "Radio stations.",
            Version = "1.2.1",
            Assembly = "NoMercy.Plugin.InternetRadio.dll",
            TargetAbi = "10.2",
            Capabilities = new PluginCapabilities
            {
                Hooks = ["ui", "scheduledTask", "metadata", "mediaSource", "libraryWrite", "encoder", "storage", "audioTools", "derivedAudio", "musicAnalysisWrite", "auth"],
                Network = new PluginNetworkCapability { Hosts = ["*.api.radio-browser.info"] },
                Rest = true,
                Ws = true,
            },
        };

        PluginManifestV3 mapped = PluginManifestV2Mapper.ToV3(v2);

        mapped.Capabilities.Select(capability => capability.Name)
            .Should()
            .BeEquivalentTo([
                "ui.mount", "scheduler", "metadata.provide", "media.source",
                "library.write", "encoder.profile", "encoder.dispatch",
                "storage.path", "audio.tools", "storage.derived",
                "music.analysis.write", "auth.claims", "network.fetch",
                "rest", "hub",
            ]);
    }

    [Fact]
    public void A_v2_manifest_warns_that_it_is_the_old_shape()
    {
        PluginManifest v2 = new()
        {
            Id = Ulid.NewUlid(),
            Name = "Old",
            Description = "Old.",
            Version = "1.0.0",
            Assembly = "Old.dll",
        };

        PluginManifestV2Mapper.ToV3(v2, out PluginRefusal notice);

        notice.Code.Should().Be(PluginRefusalCodes.ManifestV2Deprecated);
        notice.Severity.Should().Be(PluginRefusalSeverity.Warning);
    }
}
