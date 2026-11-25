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

using Moq;
using NoMercy.Api.Services;
using NoMercy.Data.ServerBackups;
using NoMercy.Queue.MediaServer.Jobs;
using NoMercyQueue.Core.Interfaces;
using Xunit;

namespace NoMercy.Tests.Api.Services;

[Trait("Category", "Unit")]
public sealed class StorageSettingsServiceTests
{
    private readonly Dictionary<string, string> _stored = [];
    private readonly Mock<IConfigurationStore> _store = new();
    private readonly Guid _userId = Guid.NewGuid();

    public StorageSettingsServiceTests()
    {
        _store
            .Setup(store => store.GetValue(It.IsAny<string>()))
            .Returns((string key) => _stored.GetValueOrDefault(key));
        _store
            .Setup(store =>
                store.SetValueAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid?>())
            )
            .Callback<string, string, Guid?>((key, value, _) => _stored[key] = value)
            .Returns(Task.CompletedTask);
    }

    private StorageSettingsService Service() =>
        new(
            new DatabaseMaintenanceSettings(_store.Object),
            new ImageCacheSettings(_store.Object),
            new BackupSettings(_store.Object),
            _store.Object
        );

    [Fact]
    public void Read_NothingStored_ReturnsTheDefaults()
    {
        StorageSettings settings = Service().Read();

        settings
            .Should()
            .Be(
                new StorageSettings(
                    DatabaseMaintenanceSettings.DefaultHour,
                    DatabaseMaintenanceSettings.DefaultFailedJobRetentionDays,
                    BackupOptions.DefaultRetainCount,
                    ImageCacheSettings.DefaultMaxMegabytes,
                    ImageCacheSettings.DefaultOrphanGraceDays,
                    ImageCacheSettings.DefaultHour,
                    false
                )
            );
    }

    [Fact]
    public async Task ApplyAsync_StoresOnlyTheGivenFields_UnderTheKeysTheJobsRead()
    {
        StorageSettingsChange change = await Service()
            .ApplyAsync(
                new StorageSettingsPatch
                {
                    FailedJobRetentionDays = 7,
                    ImageCacheMaxMegabytes = 2048,
                },
                _userId
            );

        _stored.Should().HaveCount(2);
        _stored[DatabaseMaintenanceSettings.FailedJobRetentionDaysKey].Should().Be("7");
        _stored[ImageCacheSettings.MaxMegabytesKey].Should().Be("2048");
        change.Problem.Should().BeNull();
        change.Settings!.FailedJobRetentionDays.Should().Be(7);
        change.Settings.ImageCacheMaxMegabytes.Should().Be(2048);
        change.RestartRequired.Should().BeFalse();
        _store.Verify(
            store =>
                store.SetValueAsync(
                    DatabaseMaintenanceSettings.FailedJobRetentionDaysKey,
                    "7",
                    _userId
                ),
            Times.Once
        );
    }

    [Theory]
    [InlineData("database_hour")]
    [InlineData("image_cache_hour")]
    [InlineData("backup_retain_count")]
    public async Task ApplyAsync_AHourOrTheRetainCount_NeedsARestart(string field)
    {
        StorageSettingsPatch patch = field switch
        {
            "database_hour" => new() { DatabaseHour = 5 },
            "image_cache_hour" => new() { ImageCacheHour = 5 },
            _ => new() { BackupRetainCount = 5 },
        };

        StorageSettingsChange change = await Service().ApplyAsync(patch, _userId);

        change.RestartRequired.Should().BeTrue();
    }

    [Theory]
    [InlineData(24, null, null, null, null, null)]
    [InlineData(null, 0, null, null, null, null)]
    [InlineData(null, null, 0, null, null, null)]
    [InlineData(null, null, null, -1, null, null)]
    [InlineData(null, null, null, null, 0, null)]
    [InlineData(null, null, null, null, null, -1)]
    public async Task ApplyAsync_AValueOutOfRange_ChangesNothingAtAll(
        int? databaseHour,
        int? retentionDays,
        int? retainCount,
        int? maxMegabytes,
        int? graceDays,
        int? imageHour
    )
    {
        StorageSettingsPatch patch = new()
        {
            DatabaseHour = databaseHour,
            FailedJobRetentionDays = retentionDays,
            BackupRetainCount = retainCount,
            ImageCacheMaxMegabytes = maxMegabytes,
            ImageCacheOrphanGraceDays = graceDays,
            ImageCacheHour = imageHour,
        };

        StorageSettingsChange change = await Service().ApplyAsync(patch, _userId);

        change.Problem.Should().NotBeNullOrEmpty();
        change.Settings.Should().BeNull();
        _stored.Should().BeEmpty();
    }

    [Fact]
    public async Task ApplyAsync_OneBadValueNextToAGoodOne_StoresNeither()
    {
        StorageSettingsChange change = await Service()
            .ApplyAsync(
                new StorageSettingsPatch { ImageCacheMaxMegabytes = 2048, DatabaseHour = 30 },
                _userId
            );

        change.Problem.Should().NotBeNullOrEmpty();
        _stored.Should().BeEmpty();
    }

    [Theory]
    [InlineData(true, "true")]
    [InlineData(false, "false")]
    public async Task ApplyAsync_PruneHandMadeCopies_IsStoredUnderTheKeyThePruneReads(
        bool value,
        string stored
    )
    {
        StorageSettingsChange change = await Service()
            .ApplyAsync(new StorageSettingsPatch { BackupPruneHandMadeCopies = value }, _userId);

        _stored[BackupSettings.PruneHandMadeCopiesKey].Should().Be(stored);
        change.Settings!.BackupPruneHandMadeCopies.Should().Be(value);
        change.RestartRequired.Should().BeFalse();
    }

    [Fact]
    public async Task ApplyAsync_ZeroMaxMegabytes_IsAllowedAndMeansNoLimit()
    {
        StorageSettingsChange change = await Service()
            .ApplyAsync(new StorageSettingsPatch { ImageCacheMaxMegabytes = 0 }, _userId);

        change.Problem.Should().BeNull();
        change.Settings!.ImageCacheMaxMegabytes.Should().Be(0);
    }
}
