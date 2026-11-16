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

using System.Globalization;
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
    // Longer than 32 characters, outer spaces kept: the key must match byte for byte.
    private const string LongKey = " a-forty-character-signing-key-for-tests ";

    private readonly List<string> _warnings = [];

    [Fact]
    public void Apply_ReadsTheDocumentedWorkerSettings()
    {
        IConfiguration configuration = Build(
            new()
            {
                ["Encoder:DistributedEncodingSigningKey"] = LongKey,
                ["Encoder:CoordinatorUrl"] = "https://coordinator.example:7626",
                ["Encoder:WorkerSelfBaseUrl"] = "https://worker.example:7626",
                ["Encoder:WorkerId"] = "beast-unit",
                ["Encoder:WorkerHeartbeatInterval"] = "00:00:15",
            }
        );
        EncoderOptions options = new();

        Apply(configuration, options);

        options.DistributedEncodingSigningKey.Should().Be(LongKey);
        options.CoordinatorUrl.Should().Be("https://coordinator.example:7626");
        options.WorkerSelfBaseUrl.Should().Be("https://worker.example:7626");
        options.WorkerId.Should().Be("beast-unit");
        options.WorkerHeartbeatInterval.Should().Be(TimeSpan.FromSeconds(15));
        options.IsDistributedEncodingEnabled.Should().BeTrue();
        options.IsCoordinatorMode.Should().BeFalse("a CoordinatorUrl makes this server a worker");
        _warnings.Should().BeEmpty();
    }

    [Fact]
    public void Apply_WithOnlyAKey_MakesACoordinator()
    {
        IConfiguration configuration = Build(
            new() { ["Encoder:DistributedEncodingSigningKey"] = LongKey }
        );
        EncoderOptions options = new();

        Apply(configuration, options);

        options.IsCoordinatorMode.Should().BeTrue();
    }

    [Fact]
    public void Apply_KeyShorterThan32Characters_LeavesDistributionOffAndWarns()
    {
        IConfiguration configuration = Build(
            new()
            {
                ["Encoder:DistributedEncodingSigningKey"] = new('k', 31),
                ["Encoder:CoordinatorUrl"] = "https://coordinator.example:7626",
            }
        );
        EncoderOptions options = new();

        Apply(configuration, options);

        options.DistributedEncodingSigningKey.Should().BeNull();
        options.IsDistributedEncodingEnabled.Should().BeFalse();
        _warnings
            .Should()
            .ContainSingle()
            .Which.Should()
            .Contain("Encoder:DistributedEncodingSigningKey")
            .And.Contain("32");
    }

    [Fact]
    public void Apply_KeyOfExactly32Characters_IsAccepted()
    {
        string key = new('k', EncoderDistributionSettings.MinimumSigningKeyLength);
        EncoderOptions options = new();

        Apply(Build(new() { ["Encoder:DistributedEncodingSigningKey"] = key }), options);

        options.DistributedEncodingSigningKey.Should().Be(key);
        _warnings.Should().BeEmpty();
    }

    [Fact]
    public void Apply_WithoutTheSection_KeepsTheDefaults()
    {
        EncoderOptions defaults = new();
        EncoderOptions options = new();

        Apply(Build(new()), options);

        options.DistributedEncodingSigningKey.Should().BeNull();
        options.CoordinatorUrl.Should().BeNull();
        options.WorkerSelfBaseUrl.Should().BeNull();
        options.WorkerId.Should().Be(defaults.WorkerId);
        options.WorkerHeartbeatInterval.Should().Be(defaults.WorkerHeartbeatInterval);
        options.IsDistributedEncodingEnabled.Should().BeFalse();
        _warnings.Should().BeEmpty();
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

        Apply(configuration, options);

        options.DistributedEncodingSigningKey.Should().BeNull();
        options.CoordinatorUrl.Should().BeNull();
        options.WorkerSelfBaseUrl.Should().BeNull();
        options.WorkerId.Should().Be(defaults.WorkerId);
        options.WorkerHeartbeatInterval.Should().Be(defaults.WorkerHeartbeatInterval);
        _warnings.Should().BeEmpty();
    }

    [Theory]
    [InlineData("soon")]
    [InlineData("00:00:00")]
    [InlineData("-00:00:05")]
    public void Apply_UnusableHeartbeatInterval_KeepsTheDefaultAndWarns(string interval)
    {
        EncoderOptions options = new();

        Apply(Build(new() { ["Encoder:WorkerHeartbeatInterval"] = interval }), options);

        options.WorkerHeartbeatInterval.Should().Be(new EncoderOptions().WorkerHeartbeatInterval);
        _warnings.Should().ContainSingle().Which.Should().Contain("WorkerHeartbeatInterval");
    }

    // The coordinator evicts a worker after 60 s of silence; above 30 s one lost
    // heartbeat is enough to drop it.
    [Theory]
    [InlineData("00:00:00.200", 1)]
    [InlineData("00:00:45", 30)]
    [InlineData("00:05:00", 30)]
    public void Apply_HeartbeatIntervalOutOfRange_IsClampedAndWarns(
        string interval,
        int expectedSeconds
    )
    {
        EncoderOptions options = new();

        Apply(Build(new() { ["Encoder:WorkerHeartbeatInterval"] = interval }), options);

        options.WorkerHeartbeatInterval.Should().Be(TimeSpan.FromSeconds(expectedSeconds));
        _warnings.Should().ContainSingle().Which.Should().Contain("WorkerHeartbeatInterval");
    }

    [Theory]
    [InlineData("00:00:01")]
    [InlineData("00:00:30")]
    public void Apply_HeartbeatIntervalAtTheLimits_IsKeptAsIs(string interval)
    {
        EncoderOptions options = new();

        Apply(Build(new() { ["Encoder:WorkerHeartbeatInterval"] = interval }), options);

        options
            .WorkerHeartbeatInterval.Should()
            .Be(TimeSpan.Parse(interval, CultureInfo.InvariantCulture));
        _warnings.Should().BeEmpty();
    }

    private void Apply(IConfiguration configuration, EncoderOptions options) =>
        EncoderDistributionSettings.Apply(configuration, options, _warnings.Add);

    private static IConfiguration Build(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
