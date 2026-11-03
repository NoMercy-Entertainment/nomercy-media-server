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

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NoMercy.Database;
using NoMercy.Service.Seeds;

namespace NoMercy.Tests.Service.Seeds;

public sealed class ConfigurationTransferTests
{
    [Fact]
    public async Task Failed_copy_keeps_media_configuration_for_a_later_start()
    {
        string root = NewDirectory();
        try
        {
            string mediaPath = Path.Combine(root, "media.db");
            string appPath = Path.Combine(root, "app.db");
            await CreateConfigurationTable(mediaPath);
            await CreateConfigurationTable(appPath);
            await InsertConfiguration(mediaPath, "auth_token", "secret");

            await using (AppDbContext context = CreateAppContext(appPath))
                await Assert.ThrowsAnyAsync<Exception>(() =>
                    Transfer(context, mediaPath, Path.Combine(root, "missing", "app.db"))
                );

            Assert.Equal("secret", await ReadSecureValue(mediaPath));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Copy_preserves_secure_value()
    {
        string root = NewDirectory();
        try
        {
            string mediaPath = Path.Combine(root, "media's.db");
            string appPath = Path.Combine(root, "app.db");
            await CreateConfigurationTable(mediaPath);
            await CreateConfigurationTable(appPath);
            await InsertConfiguration(mediaPath, "ssl_certificate", "certificate");

            await using (AppDbContext context = CreateAppContext(appPath))
                await Transfer(context, mediaPath, appPath);

            Assert.Equal("certificate", await ReadSecureValue(appPath));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Copy_keeps_the_app_row_when_the_key_already_exists_there()
    {
        string root = NewDirectory();
        try
        {
            string mediaPath = Path.Combine(root, "media.db");
            string appPath = Path.Combine(root, "app.db");
            await CreateConfigurationTable(mediaPath);
            await CreateConfigurationTable(appPath);
            await InsertConfiguration(mediaPath, "auth_token", "old");
            await InsertConfiguration(appPath, "auth_token", "newer");

            await using (AppDbContext context = CreateAppContext(appPath))
                await Transfer(context, mediaPath, appPath);

            Assert.Equal("newer", await ReadSecureValue(appPath));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static string NewDirectory()
    {
        string path = Path.Combine(AppContext.BaseDirectory, $"config-transfer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static AppDbContext CreateAppContext(string path)
    {
        DbContextOptionsBuilder<AppDbContext> builder = new();
        builder.UseSqlite($"Data Source={path};Pooling=False");
        return new(builder.Options);
    }

    private static async Task Transfer(AppDbContext context, string mediaPath, string appPath)
    {
        await DatabaseSeeder.MigrateConfigurationData(context, mediaPath, appPath);
    }

    private static async Task CreateConfigurationTable(string path)
    {
        await using SqliteConnection connection = new($"Data Source={path};Pooling=False");
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "CREATE TABLE Configuration (Key TEXT PRIMARY KEY, Value TEXT NOT NULL, SecureValue TEXT, ModifiedBy TEXT, CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL)";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertConfiguration(string path, string key, string secureValue)
    {
        await using SqliteConnection connection = new($"Data Source={path};Pooling=False");
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO Configuration (Key, Value, SecureValue, CreatedAt, UpdatedAt) VALUES ($key, '', $secureValue, '2026-01-01', '2026-01-01')";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$secureValue", secureValue);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string?> ReadSecureValue(string path)
    {
        await using SqliteConnection connection = new($"Data Source={path};Pooling=False");
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT SecureValue FROM Configuration LIMIT 1";
        object? value = await command.ExecuteScalarAsync();
        return value is DBNull ? null : (string?)value;
    }
}
