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
using NoMercy.MediaProcessing.Jobs;
using NoMercy.PluginSdk.Abstractions;
using NoMercyQueue.Core.Interfaces;
using Xunit;

namespace NoMercy.Tests.Repositories;

/// <summary>
/// Asking the server to encode a staged file.
///
/// <para>
/// The refusals matter as much as the acceptance. Every one of them is a case
/// that really happened and was, until this facade existed, invisible: the
/// plugin dispatched into reflection, nothing encoded, and no line anywhere told
/// the owner why. A refusal that names the reason is the whole point.
/// </para>
/// </summary>
public class PluginEncoderTests : IDisposable
{
    private static readonly Ulid LibraryId = Ulid.NewUlid();
    private static readonly Ulid FolderId = Ulid.NewUlid();
    private static readonly Ulid DriverId = Ulid.NewUlid();

    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<MediaContext> _factory;

    public PluginEncoderTests()
    {
        _connection = new("Data Source=:memory:");
        _connection.Open();

        using (SqliteCommand off = _connection.CreateCommand())
        {
            off.CommandText = "PRAGMA foreign_keys = OFF;";
            off.ExecuteNonQuery();
        }

        DbContextOptions<MediaContext> options = new DbContextOptionsBuilder<MediaContext>()
            .UseSqlite(_connection)
            .Options;

        using (MediaContext context = new(options))
        {
            context.Database.EnsureCreated();
        }

        _factory = new PooledFactory(options);
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed class PooledFactory(DbContextOptions<MediaContext> options)
        : IDbContextFactory<MediaContext>
    {
        public MediaContext CreateDbContext()
        {
            return new(options);
        }
    }

    private PluginEncoder Encoder(string? dispatched = "hash-41")
    {
        Mock<NoMercy.MediaProcessing.Jobs.IJobDispatcher> dispatcher = new();
        dispatcher
            .Setup(item => item.DispatchTracked(It.IsAny<IShouldQueue>()))
            .Returns(dispatched);

        return new(_factory, dispatcher.Object);
    }

    private async Task SeedLibraryAsync(bool withFolder)
    {
        await using MediaContext context = _factory.CreateDbContext();

        context.Libraries.Add(
            new()
            {
                Id = LibraryId,
                Title = "Television",
                Type = "tv",
            }
        );
        await context.SaveChangesAsync();

        if (!withFolder)
            return;

        context.Drivers.Add(
            new()
            {
                Id = DriverId,
                Name = "Local",
                Type = "local",
            }
        );
        context.Folders.Add(
            new()
            {
                Id = FolderId,
                Path = "/tv",
                DriverId = DriverId,
            }
        );
        await context.SaveChangesAsync();

        context.FolderLibrary.Add(new() { FolderId = FolderId, LibraryId = LibraryId });
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task AFileWithNoNameIsRefused()
    {
        PluginEncodeResult result = await Encoder().EncodeAsync("  ", LibraryId.ToString());

        result.Accepted.Should().BeFalse();
        result.Refusal.Should().Contain("no file");
    }

    [Fact]
    public async Task ALibraryIdThisServerNeverIssuedIsRefused()
    {
        PluginEncodeResult result = await Encoder().EncodeAsync("/staged/one.mkv", "not-a-ulid");

        result.Accepted.Should().BeFalse();
        result.Refusal.Should().Contain("not a library id");
    }

    [Fact]
    public async Task ALibraryTheServerDoesNotHaveIsRefused()
    {
        PluginEncodeResult result = await Encoder()
            .EncodeAsync("/staged/one.mkv", Ulid.NewUlid().ToString());

        result.Accepted.Should().BeFalse();
        result.Refusal.Should().Contain("knows no library");
    }

    // The first refusal an owner ever hits, and it used to present as an encode
    // that never started.
    [Fact]
    public async Task ALibraryWithNoFolderIsRefusedBySayingSo()
    {
        await SeedLibraryAsync(withFolder: false);

        PluginEncodeResult result = await Encoder()
            .EncodeAsync("/staged/one.mkv", LibraryId.ToString());

        result.Accepted.Should().BeFalse();
        result.Refusal.Should().Contain("no folder");
    }

    [Fact]
    public async Task AStagedFileIsQueuedAndHandsBackSomethingToFollow()
    {
        await SeedLibraryAsync(withFolder: true);

        PluginEncodeResult result = await Encoder()
            .EncodeAsync("/staged/one.mkv", LibraryId.ToString(), mediaId: "6900394");

        result.Accepted.Should().BeTrue();
        result.JobId.Should().Be("hash-41");
        result.Refusal.Should().BeNull();
    }

    /// <summary>
    /// Asking twice for the same file is how a retry behaves, so it is not an
    /// error - but a plugin holding that file has to know it has no job to
    /// follow, rather than being handed an id that answers for someone else's
    /// work.
    /// </summary>
    [Fact]
    public async Task AnEncodeAlreadyQueuedIsSaidOutLoudRatherThanSilentlyAccepted()
    {
        await SeedLibraryAsync(withFolder: true);

        PluginEncodeResult result = await Encoder(dispatched: null)
            .EncodeAsync("/staged/one.mkv", LibraryId.ToString());

        result.Accepted.Should().BeFalse();
        result.Refusal.Should().Contain("already queued");
    }
}
