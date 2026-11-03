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

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using NoMercy.Data.Services;
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;
using NoMercy.NmSystem.Dto;
using NoMercy.Providers.AcoustId;
using NoMercy.Storage;

namespace NoMercy.Tests.MediaProcessing.Music;

/// <summary>
/// Issue #397: an album path that no library folder holds ran the whole import with
/// no folder and died on a null reference before anything was stored. It is now
/// skipped, with a log that names the path.
/// </summary>
[Trait("Category", "Unit")]
public sealed class MusicLogicUnmatchedFolderTests
{
    [Fact]
    public async Task An_album_under_no_library_folder_is_skipped_with_a_log_naming_its_path()
    {
        const string albumPath = "/downloads/complete/ABBA - Arrival";
        MediaFile file = new()
        {
            Name = "01 - When I Kissed the Teacher.flac",
            Path = albumPath + "/01 - When I Kissed the Teacher.flac",
            Parsed = new() { Title = "When I Kissed the Teacher" },
        };
        MediaFolderExtend album = new()
        {
            Name = "ABBA - Arrival",
            Path = albumPath,
            Files = [file],
        };

        Folder folder = LibraryFolderResolverTests.FolderAt("/srv/music");
        Library library = LibraryFolderResolverTests.LibraryWithFolders(folder);
        IStorageFactory factory = LibraryFolderResolverTests.FactoryResolving(
            (folder, "/srv/music")
        );

        ListLogger<MusicLogic> logger = new();
        Mock<IAudioFingerprinter> fingerprinter = new();
        Mock<IDbContextFactory<MediaContext>> contextFactory = new();

        await using MusicLogic logic = new(
            logger,
            library,
            album,
            contextFactory.Object,
            factory,
            fingerprinter.Object
        );

        Func<Task> process = () => logic.Process();

        await process.Should().NotThrowAsync();
        logger
            .Entries.Should()
            .Contain(entry =>
                entry.Level == LogLevel.Warning
                && entry.Message.Contains("No library folder matches " + albumPath)
            );
        fingerprinter.VerifyNoOtherCalls();
        contextFactory.VerifyNoOtherCalls();
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => Entries.Add((logLevel, formatter(state, exception)));
    }
}
