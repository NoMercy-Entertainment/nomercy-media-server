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

using NoMercy.Data.ServerBackups;
using NoMercyQueue.Core.Interfaces;

namespace NoMercy.Tests.Service.ServerBackups;

[Trait("Category", "Unit")]
public sealed class BackupSettingsTests : IDisposable
{
    private readonly Dictionary<string, string> _values = [];
    private readonly BackupTestFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void RetainCount_WithNoSetting_IsTheSmallDefault()
    {
        CreateSettings().RetainCount.Should().Be(BackupOptions.DefaultRetainCount);
        BackupOptions.DefaultRetainCount.Should().Be(3);
    }

    [Fact]
    public void RetainCount_FollowsTheSetting()
    {
        _values[BackupSettings.RetainCountKey] = "7";

        CreateSettings().RetainCount.Should().Be(7);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-2")]
    [InlineData("many")]
    [InlineData("")]
    public void RetainCount_WithAnUnusableValue_FallsBackToTheDefault(string value)
    {
        _values[BackupSettings.RetainCountKey] = value;

        CreateSettings().RetainCount.Should().Be(BackupOptions.DefaultRetainCount);
    }

    [Fact]
    public async Task TheSetting_DecidesHowManySetsAreKept()
    {
        _folder.CreateServer();
        _values[BackupSettings.RetainCountKey] = "2";
        BackupService service = BackupTestFolder.CreateBackupService(
            _folder.Options(CreateSettings().RetainCount)
        );

        for (int i = 0; i < 4; i++)
            await service.CreateAsync("daily scheduled backup", CancellationToken.None);

        (await service.ListAsync(CancellationToken.None)).Should().HaveCount(2);
    }

    private BackupSettings CreateSettings()
    {
        Mock<IConfigurationStore> store = new();
        store
            .Setup(x => x.GetValue(It.IsAny<string>()))
            .Returns((string key) => _values.GetValueOrDefault(key));
        return new(store.Object);
    }
}
