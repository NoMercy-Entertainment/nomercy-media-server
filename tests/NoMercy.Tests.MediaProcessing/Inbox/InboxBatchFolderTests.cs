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
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;
using NoMercy.Events;
using NoMercy.Events.FileWatcher;
using NoMercy.Events.Inbox;
using NoMercy.MediaProcessing.EventHandlers;
using NoMercy.MediaProcessing.Inbox;
using NoMercy.MediaProcessing.Shows;
using NoMercy.NmSystem.Domain;
using NoMercy.Storage;
using NoMercy.Storage.Drivers.Local;
using NoMercy.Storage.Factory;
using NoMercy.Tests.MediaProcessing.Files;

namespace NoMercy.Tests.MediaProcessing.Inbox;

/// <summary>
/// A release dropped in the inbox arrives as a folder holding the episode files
/// ("[Group] Show [BD 1080p]/[Group] Show - 01 [BD 1080p].mkv"), so the inbox's
/// top-level child is the folder, and the folder has no video extension.
/// </summary>
[Trait("Category", "Integration")]
public class InboxBatchFolderTests
{
    [Theory]
    [InlineData(
        "[GFD] Battle Programmer Shirase [BD 1080p x264 FLAC]",
        "[GFD] Battle Programmer Shirase - 01 [BD 1080p x264 FLAC].mkv",
        "Battle Programmer Shirase"
    )]
    [InlineData(
        "[SubsPlease] Kamiina Botan, Yoeru Sugata wa Yuri no Hana (01-12) (1080p) [Batch]",
        "[SubsPlease] Kamiina Botan, Yoeru Sugata wa Yuri no Hana - 01 (1080p) [E38EAFF4].mkv",
        "Kamiina Botan, Yoeru Sugata wa Yuri no Hana"
    )]
    public async Task FolderDroppedInTheInbox_IsClassifiedByItsEpisodeFiles(
        string folderName,
        string fileName,
        string title
    )
    {
        string tempRoot = Path.Combine(
            Path.GetTempPath(),
            "nm-inbox-batch-" + Path.GetRandomFileName()
        );
        string inboxDir = Path.Combine(tempRoot, "inbox");
        string batchDir = Path.Combine(inboxDir, folderName);
        Directory.CreateDirectory(batchDir);
        string episodePath = Path.Combine(batchDir, fileName);
        await File.WriteAllBytesAsync(episodePath, [0x1A, 0x45, 0xDF, 0xA3]);

        SqliteConnection connection = new($"DataSource={Guid.NewGuid()};Mode=Memory;Cache=Shared");
        connection.Open();

        try
        {
            Mock<IDriverConfigResolver> resolverMock = new();
            resolverMock
                .Setup(r => r.Resolve(It.IsAny<Ulid>()))
                .Returns(("local", "{\"rootPath\":\"\"}"));
            StorageFactory storageFactory = new(
                new LocalStorageDriver(),
                NullLogger<StorageFactory>.Instance,
                resolverMock.Object
            );

            DbContextOptions<MediaContext> options = new DbContextOptionsBuilder<MediaContext>()
                .UseSqlite(connection)
                .Options;
            MediaContext ContextFactory() => new(options);

            Ulid libraryId = Ulid.NewUlid();
            Ulid folderId = Ulid.NewUlid();
            await using (MediaContext seed = ContextFactory())
            {
                seed.Database.EnsureCreated();
                seed.Database.ExecuteSqlRaw("PRAGMA foreign_keys = OFF;");
                seed.Libraries.Add(
                    new()
                    {
                        Id = libraryId,
                        Title = "Inbox",
                        Type = MediaTypes.InboxMediaType,
                    }
                );
                seed.Folders.Add(
                    new()
                    {
                        Id = folderId,
                        Path = inboxDir,
                        DriverId = Ulid.NewUlid(),
                    }
                );
                seed.FolderLibrary.Add(new() { LibraryId = libraryId, FolderId = folderId });
                await seed.SaveChangesAsync();
            }

            Mock<IEventBus> eventBus = new();
            eventBus
                .Setup(bus =>
                    bus.Subscribe<FileCreatedEvent>(
                        It.IsAny<Func<FileCreatedEvent, CancellationToken, Task>>()
                    )
                )
                .Returns(Mock.Of<IDisposable>());
            eventBus
                .Setup(bus =>
                    bus.PublishAsync(
                        It.IsAny<InboxItemDetectedEvent>(),
                        It.IsAny<CancellationToken>()
                    )
                )
                .Returns(Task.CompletedTask);

            Mock<IInboxMetadataProbe> probe = new();
            probe
                .Setup(p =>
                    p.SearchMoviesAsync(
                        It.IsAny<string>(),
                        It.IsAny<int?>(),
                        It.IsAny<CancellationToken>()
                    )
                )
                .ReturnsAsync([]);
            probe
                .Setup(p =>
                    p.SearchTvAsync(
                        It.IsAny<string>(),
                        It.IsAny<int?>(),
                        It.IsAny<CancellationToken>()
                    )
                )
                .ReturnsAsync([]);
            Mock<IMediaTypeClassifier> mediaType = new();
            mediaType
                .Setup(c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<int?>()))
                .ReturnsAsync("anime");

            InboxClassifierEventHandler handler = new(
                NullLogger<InboxClassifierEventHandler>.Instance,
                eventBus.Object,
                new(
                    probe.Object,
                    new Mock<IInboxAudioTagReader>().Object,
                    mediaType.Object,
                    TestFilenameParser.Default
                ),
                new(storageFactory, new()),
                ContextFactory,
                storageFactory
            );

            await handler.OnFileCreated(
                new()
                {
                    FolderPath = inboxDir,
                    LibraryId = libraryId,
                    LibraryType = MediaTypes.InboxMediaType,
                },
                CancellationToken.None
            );

            probe.Verify(
                p => p.SearchTvAsync(title, It.IsAny<int?>(), It.IsAny<CancellationToken>()),
                Times.Once
            );
        }
        finally
        {
            connection.Dispose();
            try
            {
                Directory.Delete(tempRoot, recursive: true);
            }
            catch
            {
                // best-effort temp cleanup
            }
        }
    }
}
