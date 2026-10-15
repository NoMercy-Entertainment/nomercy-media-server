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
using Moq;
using NoMercy.Database;
using NoMercy.Database.Models.Media;
using NoMercy.MediaProcessing.EventHandlers;
using NoMercy.MediaProcessing.Files;
using NoMercy.Storage;

namespace NoMercy.Tests.MediaProcessing.Files;

public class SupersededVideoFilesTests : IDisposable
{
    private static IStorageDriver Driver => new Mock<IStorageDriver>(MockBehavior.Loose).Object;

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MediaContext> _options;

    public SupersededVideoFilesTests()
    {
        _connection = new("Data Source=:memory:");
        _connection.Open();

        using (SqliteCommand foreignKeys = _connection.CreateCommand())
        {
            foreignKeys.CommandText = "PRAGMA foreign_keys = OFF;";
            foreignKeys.ExecuteNonQuery();
        }

        _options = new DbContextOptionsBuilder<MediaContext>().UseSqlite(_connection).Options;
        using MediaContext context = new(_options);
        context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private static VideoFile Row(int? episodeId, string hostFolder, string filename) =>
        new()
        {
            EpisodeId = episodeId,
            Share = "shareA",
            HostFolder = hostFolder,
            Filename = filename,
            Quality = "1080",
            Languages = "[]",
        };

    private async Task<List<VideoFile>> AllAsync()
    {
        await using MediaContext context = new(_options);
        return await context.VideoFiles.AsNoTracking().ToListAsync();
    }

    [Fact]
    public async Task ReEncodeWithNewTitle_RemovesTheOldRowOfThatEpisodeOnly()
    {
        await using (MediaContext seed = new(_options))
        {
            seed.VideoFiles.AddRange(
                Row(1, "/tv/Show.S01E03", "/Show.S01E03.Episode.3.NoMercy.m3u8"),
                Row(1, "/tv/Show.S01E03", "/Show.S01E03.Real.Title.NoMercy.m3u8"),
                Row(2, "/tv/Show.S01E03", "/Show.S01E03.Other.Episode.m3u8"),
                Row(1, "/tv/Elsewhere", "/Show.S01E03.Other.Folder.m3u8")
            );
            await seed.SaveChangesAsync();
        }

        await using MediaContext context = new(_options);
        int removed = await new FileRepository(context, Driver).DeleteSupersededVideoFilesAsync(
            1,
            false,
            [new("shareA", "/tv/Show.S01E03", "/Show.S01E03.Real.Title.NoMercy.m3u8")]
        );

        removed.Should().Be(1);
        List<VideoFile> remaining = await AllAsync();
        remaining.Should().HaveCount(3);
        remaining.Should().NotContain(vf => vf.Filename.Contains("Episode.3"));
    }

    [Fact]
    public async Task DeleteByPath_MatchesAHostFolderGivenWithBackslashes()
    {
        await using (MediaContext seed = new(_options))
        {
            seed.VideoFiles.Add(Row(1, @"C:\media\Show.S01E03", "/a.m3u8"));
            await seed.SaveChangesAsync();
        }

        await using MediaContext context = new(_options);
        int deleted = await new FileRepository(context, Driver).DeleteVideoFileByPathAsync(
            @"C:\media\Show.S01E03",
            "/a.m3u8"
        );

        deleted.Should().Be(1);
    }

    [Fact]
    public void HostFolderOf_UsesForwardSlashes()
    {
        FileWatcherEventHandler
            .HostFolderOf("C:/media/Show.S01E03/a.m3u8")
            .Should()
            .Be("C:/media/Show.S01E03");
    }
}
