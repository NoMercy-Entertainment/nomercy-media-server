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
using NoMercy.Queue.MediaServer.Jobs;
using NoMercyQueue.Core.Interfaces;
using Xunit;

namespace NoMercy.Tests.Queue;

public class ImageCacheSettingsTests
{
    private readonly Dictionary<string, string> _values = [];

    private ImageCacheSettings Create()
    {
        Mock<IConfigurationStore> store = new();
        store
            .Setup(x => x.GetValue(It.IsAny<string>()))
            .Returns((string key) => _values.GetValueOrDefault(key));
        return new(store.Object);
    }

    [Fact]
    public void Defaults_ApplyWhenNothingIsConfigured()
    {
        ImageCacheSettings settings = Create();

        settings.MaxBytes.Should().Be(ImageCacheSettings.DefaultMaxMegabytes * 1024L * 1024L);
        settings
            .OrphanGrace.Should()
            .Be(TimeSpan.FromDays(ImageCacheSettings.DefaultOrphanGraceDays));
        settings.Hour.Should().Be(ImageCacheSettings.DefaultHour);
    }

    [Fact]
    public void MaxBytes_FollowsTheConfiguredMegabytes()
    {
        _values[ImageCacheSettings.MaxMegabytesKey] = "512";

        Create().MaxBytes.Should().Be(512L * 1024 * 1024);
    }

    [Fact]
    public void MaxBytes_ZeroMeansNoLimit()
    {
        _values[ImageCacheSettings.MaxMegabytesKey] = "0";

        Create().MaxBytes.Should().Be(0);
    }

    [Theory]
    [InlineData("-5")]
    [InlineData("lots")]
    [InlineData("")]
    public void MaxBytes_UnusableValueFallsBackToTheDefault(string raw)
    {
        _values[ImageCacheSettings.MaxMegabytesKey] = raw;

        Create().MaxBytes.Should().Be(ImageCacheSettings.DefaultMaxMegabytes * 1024L * 1024L);
    }

    [Fact]
    public void OrphanGrace_FollowsTheConfiguredDays()
    {
        _values[ImageCacheSettings.OrphanGraceDaysKey] = "3";

        Create().OrphanGrace.Should().Be(TimeSpan.FromDays(3));
    }

    [Fact]
    public void Hour_OutOfRangeFallsBackToTheDefault()
    {
        _values[ImageCacheSettings.HourKey] = "25";

        Create().Hour.Should().Be(ImageCacheSettings.DefaultHour);
    }
}
