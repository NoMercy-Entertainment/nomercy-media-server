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

using Microsoft.Extensions.Configuration;
using NoMercy.Encoder.Composition;
using NoMercy.Service.Configuration;
using Xunit;

namespace NoMercy.Tests.Service.Configuration;

/// <summary>
/// The distributed-encoding settings docs/distributed-encoding.md documents under
/// the "Encoder" section must reach EncoderOptions, or a worker never registers (#228).
/// </summary>
[Trait("Category", "Unit")]
public class EncoderDistributionSettingsTests
{
    [Fact]
    public void Apply_ReadsTheDocumentedWorkerSettings()
    {
        IConfiguration configuration = Build(
            new()
            {
                ["Encoder:DistributedEncodingSigningKey"] = " key with spaces kept ",
                ["Encoder:CoordinatorUrl"] = "https://coordinator.example:7626",
                ["Encoder:WorkerSelfBaseUrl"] = "https://worker.example:7626",
                ["Encoder:WorkerId"] = "beast-unit",
                ["Encoder:WorkerHeartbeatInterval"] = "00:00:15",
            }
        );
        EncoderOptions options = new();

        EncoderDistributionSettings.Apply(configuration, options);

        options.DistributedEncodingSigningKey.Should().Be(" key with spaces kept ");
        options.CoordinatorUrl.Should().Be("https://coordinator.example:7626");
        options.WorkerSelfBaseUrl.Should().Be("https://worker.example:7626");
        options.WorkerId.Should().Be("beast-unit");
        options.WorkerHeartbeatInterval.Should().Be(TimeSpan.FromSeconds(15));
        options.IsDistributedEncodingEnabled.Should().BeTrue();
        options.IsCoordinatorMode.Should().BeFalse("a CoordinatorUrl makes this server a worker");
    }

    [Fact]
    public void Apply_WithOnlyAKey_MakesACoordinator()
    {
        IConfiguration configuration = Build(
            new() { ["Encoder:DistributedEncodingSigningKey"] = "coordinator-key" }
        );
        EncoderOptions options = new();

        EncoderDistributionSettings.Apply(configuration, options);

        options.IsCoordinatorMode.Should().BeTrue();
    }

    [Fact]
    public void Apply_WithoutTheSection_KeepsTheDefaults()
    {
        EncoderOptions defaults = new();
        EncoderOptions options = new();

        EncoderDistributionSettings.Apply(Build(new()), options);

        options.DistributedEncodingSigningKey.Should().BeNull();
        options.CoordinatorUrl.Should().BeNull();
        options.WorkerSelfBaseUrl.Should().BeNull();
        options.WorkerId.Should().Be(defaults.WorkerId);
        options.WorkerHeartbeatInterval.Should().Be(defaults.WorkerHeartbeatInterval);
        options.IsDistributedEncodingEnabled.Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Apply_BlankValues_KeepTheDefaults(string blank)
    {
        IConfiguration configuration = Build(
            new()
            {
                ["Encoder:DistributedEncodingSigningKey"] = blank,
                ["Encoder:CoordinatorUrl"] = blank,
                ["Encoder:WorkerSelfBaseUrl"] = blank,
                ["Encoder:WorkerId"] = blank,
                ["Encoder:WorkerHeartbeatInterval"] = blank,
            }
        );
        EncoderOptions defaults = new();
        EncoderOptions options = new();

        EncoderDistributionSettings.Apply(configuration, options);

        options.DistributedEncodingSigningKey.Should().BeNull();
        options.CoordinatorUrl.Should().BeNull();
        options.WorkerSelfBaseUrl.Should().BeNull();
        options.WorkerId.Should().Be(defaults.WorkerId);
        options.WorkerHeartbeatInterval.Should().Be(defaults.WorkerHeartbeatInterval);
    }

    [Theory]
    [InlineData("soon")]
    [InlineData("00:00:00")]
    [InlineData("-00:00:05")]
    public void Apply_UnusableHeartbeatInterval_KeepsTheDefault(string interval)
    {
        IConfiguration configuration = Build(
            new() { ["Encoder:WorkerHeartbeatInterval"] = interval }
        );
        EncoderOptions options = new();

        EncoderDistributionSettings.Apply(configuration, options);

        options.WorkerHeartbeatInterval.Should().Be(new EncoderOptions().WorkerHeartbeatInterval);
    }

    private static IConfiguration Build(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
