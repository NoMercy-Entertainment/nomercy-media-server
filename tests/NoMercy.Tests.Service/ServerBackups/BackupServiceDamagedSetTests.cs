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
using NoMercy.Data.ServerBackups;

namespace NoMercy.Tests.Service.ServerBackups;

[Trait("Category", "Unit")]
public sealed class BackupServiceDamagedSetTests : IDisposable
{
    private readonly BackupTestFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private static void SetMarker(string path, string content)
    {
        using SqliteConnection connection = new($"Data Source={path};Pooling=False");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE marker SET content = $content";
        command.Parameters.AddWithValue("$content", content);
        command.ExecuteNonQuery();
    }

    [Fact]
    public async Task DamagedSets_NeverTakeAGoodSetsPlace_AndArePrunedAmongThemselves()
    {
        _folder.CreateServer();
        BackupOptions options = _folder.Options(retainCount: 2);
        BackupService service = BackupTestFolder.CreateBackupService(options);
        string goodA = (await service.CreateAsync("a", CancellationToken.None)).Id;
        string goodB = (await service.CreateAsync("b", CancellationToken.None)).Id;

        // Three request-and-cancel cycles on a damaged server: three damaged sets.
        SetMarker(_folder.DatabasePath("media"), MarkerDamageSqlite.CorruptMarker);
        BackupSetWriter damagedWriter = BackupTestFolder.CreateWriter(
            options,
            new MarkerDamageSqlite()
        );
        List<string> damaged = [];
        for (int index = 0; index < 3; index++)
            damaged.Add(
                (
                    await damagedWriter.WriteAsync(
                        $"before restoring something {index}",
                        options.Databases,
                        options.ConfigRoot,
                        keepDamagedCopies: true,
                        CancellationToken.None
                    )
                ).Id
            );

        SetMarker(_folder.DatabasePath("media"), "repaired");
        string goodC = (await service.CreateAsync("c", CancellationToken.None)).Id;

        string Folder(string id) => Path.Combine(options.SetsRoot, id);
        Directory.Exists(Folder(goodC)).Should().BeTrue();
        Directory.Exists(Folder(goodB)).Should().BeTrue("damaged sets must not push it out");
        Directory.Exists(Folder(goodA)).Should().BeFalse();
        Directory.Exists(Folder(damaged[2])).Should().BeTrue();
        Directory.Exists(Folder(damaged[1])).Should().BeTrue();
        Directory.Exists(Folder(damaged[0])).Should().BeFalse();
    }
}
