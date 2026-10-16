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
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using NoMercy.Data.Plugins;
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Storage;

namespace NoMercy.Tests.Repositories.Plugins;

/// <summary>
/// A plugin asks how much room is left on a folder. The answer has to be the
/// file system that holds the folder, which on Linux is often a disk or a
/// Docker volume of its own, not the root file system.
/// </summary>
[Trait("Category", "Unit")]
public sealed class PluginFolderFreeSpaceProbeTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MediaContext> _options;

    public PluginFolderFreeSpaceProbeTests()
    {
        _connection = new("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<MediaContext>().UseSqlite(_connection).Options;

        using MediaContext context = new(_options);
        context.Database.EnsureCreated();

        using SqliteCommand relax = _connection.CreateCommand();
        relax.CommandText = "PRAGMA foreign_keys = OFF;";
        relax.ExecuteNonQuery();
    }

    public void Dispose() => _connection.Dispose();

    [SkippableFact]
    public void Off_windows_a_folder_is_measured_where_it_is_not_at_the_root()
    {
        Skip.If(
            OperatingSystem.IsWindows(),
            "Windows measures the drive root, which is right there."
        );

        PluginFolderFreeSpaceProbe
            .MeasuredPath("/mnt/media/movies", _ => true)
            .Should()
            .Be("/mnt/media/movies");
    }

    /// <summary>A folder that does not exist yet is measured at its nearest parent that does.</summary>
    [SkippableFact]
    public void Off_windows_a_missing_folder_is_measured_at_its_nearest_existing_parent()
    {
        Skip.If(
            OperatingSystem.IsWindows(),
            "Windows measures the drive root, which is right there."
        );

        PluginFolderFreeSpaceProbe
            .MeasuredPath("/mnt/media/new/sub", path => path == "/mnt/media")
            .Should()
            .Be("/mnt/media");
        PluginFolderFreeSpaceProbe.MeasuredPath("/nowhere/at/all", _ => false).Should().Be("/");
    }

    [SkippableFact]
    public void On_windows_a_folder_is_measured_at_its_drive()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "The drive root is the Windows answer.");

        PluginFolderFreeSpaceProbe.MeasuredPath(@"D:\Media\Movies", _ => true).Should().Be(@"D:\");
    }

    /// <summary>
    /// The real case: /dev/shm is a file system of its own (tmpfs) on Linux.
    /// A folder on it must get its free space, not the root's.
    /// </summary>
    [SkippableFact]
    public async Task On_linux_a_folder_on_its_own_file_system_gets_that_file_systems_free_space()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "Needs a Linux mount other than the root.");
        Skip.IfNot(Directory.Exists("/dev/shm"), "No /dev/shm on this machine.");

        DriveInfo shm = new("/dev/shm");
        DriveInfo root = new("/");
        Skip.If(shm.TotalSize == root.TotalSize, "/dev/shm is not a file system of its own here.");

        string folder = Path.Combine("/dev/shm", $"nm-free-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);

        try
        {
            Ulid folderId = Ulid.NewUlid();
            await using (MediaContext context = new(_options))
            {
                context.Drivers.Add(
                    new Driver
                    {
                        Id = Ulid.NewUlid(),
                        Name = "Local",
                        Type = "local",
                    }
                );
                await context.SaveChangesAsync();
                context.Folders.Add(
                    new Folder
                    {
                        Id = folderId,
                        Path = folder,
                        DriverId = context.Drivers.Single().Id,
                    }
                );
                await context.SaveChangesAsync();
            }

            long free = await Probe().FreeBytesAsync(folderId.ToString());

            free.Should().BeGreaterThan(0);
            free.Should()
                .BeLessThanOrEqualTo(shm.TotalSize, "the folder lives on /dev/shm, not on /");
        }
        finally
        {
            Directory.Delete(folder);
        }
    }

    private PluginFolderFreeSpaceProbe Probe()
    {
        Mock<IDbContextFactory<MediaContext>> factory = new();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new(_options));

        return new(factory.Object);
    }
}
