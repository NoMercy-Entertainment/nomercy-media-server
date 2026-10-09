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
using NoMercy.Database.Models.Libraries;
using NoMercy.MediaProcessing.Files;

namespace NoMercy.Tests.MediaProcessing.Files;

/// <summary>
/// A show file whose season/episode pair matches no episode is still stored, with no
/// episode and no movie on it. Nothing can list or play such a row, and the library
/// listing only returns shows that have an episode with a playable file — so the show
/// vanishes and the library renders empty.
///
/// Verified live on v0.1.454 (2026-08-02): an Anime library with two shows imported
/// 24 episodes and 2 video files, both files were <c>S00E00</c> specials that exist in
/// no provider episode list, both rows landed with a null EpisodeId, and the library
/// endpoint answered <c>NMGrid</c> with <c>items: []</c>. Seven music folders that
/// failed the same day each wrote an ImportFailure row; these two wrote nothing, so
/// there was no screen anywhere that could explain the empty library.
/// </summary>
[Trait("Category", "Unit")]
public sealed class UnmatchedEpisodeFileIsReportedTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MediaContext> _options;

    public UnmatchedEpisodeFileIsReportedTests()
    {
        _connection = new("Data Source=:memory:");
        _connection.Open();

        using (SqliteCommand foreignKeysOff = _connection.CreateCommand())
        {
            foreignKeysOff.CommandText = "PRAGMA foreign_keys = OFF;";
            foreignKeysOff.ExecuteNonQuery();
        }

        _options = new DbContextOptionsBuilder<MediaContext>().UseSqlite(_connection).Options;
        using MediaContext context = new(_options);
        context.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private FileRepository BuildRepository(MediaContext context) =>
        new(context, new NoMercy.Storage.Drivers.Local.LocalStorageDriver());

    [Fact]
    public async Task AnUnmatchedShowFile_IsRecordedAgainstItsLibrary()
    {
        Ulid libraryId = Ulid.NewUlid();
        const string filePath = "/media/Anime/No-Rin.(2014)/No-Rin.S00E00/No-Rin.S00E00.m3u8";

        await using (MediaContext context = new(_options))
        {
            await BuildRepository(context)
                .RecordUnmatchedEpisodeFileAsync(
                    filePath,
                    libraryId,
                    "No episode matches season 0 episode 0 of No-Rin."
                );
        }

        await using (MediaContext context = new(_options))
        {
            ImportFailure failure = await context.ImportFailures.SingleAsync();

            Assert.Equal(filePath, failure.FilePath);
            Assert.Equal(libraryId, failure.LibraryId);
            Assert.False(failure.Resolved);
            Assert.Contains("No episode matches", failure.ErrorMessage);
        }
    }

    /// <summary>
    /// A rescan re-walks every file, so the same unmatched file is reported again on
    /// every pass. That must bump the existing row rather than grow the list the user
    /// reads, or the failures screen fills with duplicates of one problem.
    /// </summary>
    [Fact]
    public async Task ReportingTheSameFileTwice_BumpsTheRetryCountInsteadOfDuplicating()
    {
        Ulid libraryId = Ulid.NewUlid();
        const string filePath = "/media/Anime/Rail.Wars!.(2014)/Rail.Wars!.S00E00/file.m3u8";

        for (int pass = 0; pass < 2; pass++)
        {
            await using MediaContext context = new(_options);
            await BuildRepository(context)
                .RecordUnmatchedEpisodeFileAsync(filePath, libraryId, "No episode matches.");
        }

        await using (MediaContext context = new(_options))
        {
            // SingleAsync is the assertion that matters: one problem, one row. The count
            // is a retry counter, so the first report leaves it at zero and the second
            // pass takes it to one.
            ImportFailure failure = await context.ImportFailures.SingleAsync();

            Assert.Equal(1, failure.RetryCount);
        }
    }
}
