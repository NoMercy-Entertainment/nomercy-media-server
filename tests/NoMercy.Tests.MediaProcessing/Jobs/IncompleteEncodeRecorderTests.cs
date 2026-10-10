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
using NoMercy.Database.Models.Encoder;
using NoMercy.MediaProcessing.Jobs.MediaJobs;

namespace NoMercy.Tests.MediaProcessing.Jobs;

[Trait("Category", "Unit")]
public sealed class IncompleteEncodeRecorderTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly MediaContext _context;
    private readonly IncompleteEncodeRecorder _recorder;

    public IncompleteEncodeRecorderTests()
    {
        string dbName = Guid.NewGuid().ToString();
        _connection = new($"DataSource={dbName};Mode=Memory;Cache=Shared");
        _connection.Open();

        DbContextOptions<MediaContext> options = new DbContextOptionsBuilder<MediaContext>()
            .UseSqlite(_connection)
            .Options;

        _context = new(options);
        _context.Database.EnsureCreated();
        _context.Database.ExecuteSqlRaw("PRAGMA foreign_keys = OFF;");

        _recorder = new();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task RoundTrip_MissingRenditions_SplitToExpectedCount()
    {
        string[] keys = ["video-1080p", "video-720p", "audio-aac"];

        await _recorder.RecordAsync(
            _context,
            mediaId: 42L,
            folderId: "folder-abc",
            title: "Test Movie",
            missingKeys: keys,
            lastError: null,
            attemptsMade: 1,
            ct: CancellationToken.None
        );

        IncompleteEncode? row = await _context
            .IncompleteEncodes.AsNoTracking()
            .FirstOrDefaultAsync(r => r.MediaId == 42L && r.FolderId == "folder-abc");

        row.Should().NotBeNull();
        string[] split = row!.MissingRenditions.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        split.Should().HaveCount(3);
        split.Should().BeEquivalentTo(keys);
    }

    [Fact]
    public async Task RecordAsync_ThenClearAsync_LeavesNoRow()
    {
        await _recorder.RecordAsync(
            _context,
            mediaId: 7L,
            folderId: "folder-xyz",
            title: "Some Show S01E01",
            missingKeys: ["video-480p"],
            lastError: "timeout",
            attemptsMade: 2,
            ct: CancellationToken.None
        );

        await _recorder.ClearAsync(
            _context,
            mediaId: 7L,
            folderId: "folder-xyz",
            ct: CancellationToken.None
        );

        IncompleteEncode? row = await _context
            .IncompleteEncodes.AsNoTracking()
            .FirstOrDefaultAsync(r => r.MediaId == 7L && r.FolderId == "folder-xyz");

        row.Should().BeNull();
    }

    [Fact]
    public async Task RecordAsync_CalledTwice_UpdatesRowNotDuplicates()
    {
        string[] firstKeys = ["video-1080p", "video-720p"];
        string[] secondKeys = ["video-480p"];

        await _recorder.RecordAsync(
            _context,
            mediaId: 99L,
            folderId: "folder-dup",
            title: "Dup Movie",
            missingKeys: firstKeys,
            lastError: "error-1",
            attemptsMade: 1,
            ct: CancellationToken.None
        );

        await _recorder.RecordAsync(
            _context,
            mediaId: 99L,
            folderId: "folder-dup",
            title: "Dup Movie",
            missingKeys: secondKeys,
            lastError: "error-2",
            attemptsMade: 2,
            ct: CancellationToken.None
        );

        int count = await _context.IncompleteEncodes.CountAsync(r =>
            r.MediaId == 99L && r.FolderId == "folder-dup"
        );

        count
            .Should()
            .Be(1, "second RecordAsync must update the existing row, not insert a duplicate");

        IncompleteEncode? row = await _context
            .IncompleteEncodes.AsNoTracking()
            .FirstOrDefaultAsync(r => r.MediaId == 99L && r.FolderId == "folder-dup");

        row!.AttemptsMade.Should().Be(2);
        row.LastError.Should().Be("error-2");
        string[] split = row.MissingRenditions.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        split.Should().BeEquivalentTo(secondKeys);
    }

    [Fact]
    public async Task RecordFailureAsync_CalledTwice_CountsAttemptsOnOneRow()
    {
        await _recorder.RecordFailureAsync(
            _context,
            mediaId: 55L,
            folderId: "folder-attempts",
            title: "Retry Movie",
            missingKeys: ["video-1080p"],
            lastError: "error-1",
            ct: CancellationToken.None
        );

        int afterFirst = await _context
            .IncompleteEncodes.AsNoTracking()
            .Where(r => r.MediaId == 55L && r.FolderId == "folder-attempts")
            .Select(r => r.AttemptsMade)
            .SingleAsync();
        afterFirst.Should().Be(1);

        await _recorder.RecordFailureAsync(
            _context,
            mediaId: 55L,
            folderId: "folder-attempts",
            title: "Retry Movie",
            missingKeys: ["video-720p"],
            lastError: "error-2",
            ct: CancellationToken.None
        );

        List<IncompleteEncode> rows = await _context
            .IncompleteEncodes.AsNoTracking()
            .Where(r => r.MediaId == 55L && r.FolderId == "folder-attempts")
            .ToListAsync();

        rows.Should().ContainSingle();
        rows[0].AttemptsMade.Should().Be(2);
        rows[0].LastError.Should().Be("error-2");
    }
}
