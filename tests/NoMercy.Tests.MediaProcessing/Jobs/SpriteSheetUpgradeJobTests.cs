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

using Microsoft.Extensions.DependencyInjection;
using Moq;
using NoMercy.Encoder.PostProcess;
using NoMercy.Encoder.Progress;
using NoMercy.Events;
using NoMercy.MediaProcessing.Files;
using NoMercy.MediaProcessing.Jobs.MediaJobs;
using NoMercy.Storage;

namespace NoMercy.Tests.MediaProcessing.Jobs;

/// <summary>
/// The whole chain a blank scrub preview goes through: what the folder holds,
/// whether that is worth rebuilding, and whether the rebuilt pair ends up named
/// where a client will look for it.
///
/// <para>A title encoded before the tile size went into the filename carries
/// <c>sprite.webp</c> and no cue file, which is what 281 of 840 playables on the
/// dev library looked like — the sweep could not see them and no client had a
/// preview to draw.</para>
/// </summary>
public class SpriteSheetUpgradeJobTests
{
    private const string HostFolder = "/mnt/library/Anime/Show/Show.S01E01";
    private static readonly Ulid FolderId = Ulid.NewUlid();
    private static readonly Ulid DriverId = Ulid.NewUlid();

    private static StorageEntry File(string name) =>
        new($"{HostFolder}/{name}", false, 1024, DateTimeOffset.UnixEpoch);

    private sealed record Harness(
        SpriteSheetUpgradeJob Job,
        Mock<ISpriteSheetRefresher> Refresher,
        Mock<IFileRepository> Files
    );

    private static Harness JobFor(params string[] fileNames)
    {
        if (!EventBusProvider.IsConfigured)
            EventBusProvider.Configure(new InMemoryEventBus());

        Mock<IStorage> storage = new(MockBehavior.Loose);
        storage.Setup(s => s.Exists(HostFolder)).Returns(true);
        storage
            .Setup(s => s.List(HostFolder, null, false))
            .Returns(fileNames.Select(File).ToList());
        storage
            .Setup(s => s.GetName(It.IsAny<string>()))
            .Returns((string path) => path[(path.LastIndexOf('/') + 1)..]);

        Mock<IStorageFactory> factory = new(MockBehavior.Loose);
        factory.Setup(f => f.For(FolderId, DriverId, string.Empty)).Returns(storage.Object);

        Mock<ISpriteSheetRefresher> refresher = new(MockBehavior.Loose);
        refresher
            .Setup(r =>
                r.RefreshAsync(
                    It.IsAny<IStorage>(),
                    HostFolder,
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<Action<EncodingProgress>?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync("thumbs_320x180.webp");

        Mock<IFileRepository> files = new(MockBehavior.Loose);
        files
            .Setup(f =>
                f.RepointPreviewTracksAsync(
                    HostFolder,
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(1);

        ServiceCollection services = [];
        services.AddSingleton(refresher.Object);
        services.AddSingleton(factory.Object);
        services.AddSingleton(files.Object);

        SpriteSheetUpgradeJob job = new(
            FolderId.ToString(),
            DriverId.ToString(),
            HostFolder,
            "Show"
        );
        job.InjectStorageServices(services.BuildServiceProvider());

        return new(job, refresher, files);
    }

    [Fact]
    public async Task Rebuilds_the_preview_for_a_sheet_that_predates_the_tile_size_name()
    {
        Harness harness = JobFor("sprite.webp", "chapters.vtt", "Show.S01E01.mkv");

        await harness.Job.Handle();

        harness.Refresher.Verify(
            r =>
                r.RefreshAsync(
                    It.IsAny<IStorage>(),
                    HostFolder,
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<Action<EncodingProgress>?>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once,
            "a legacy sheet states no tile size, and skipping it left the folder "
                + "looking like it held no preview to rebuild"
        );
    }

    [Fact]
    public async Task Names_the_rebuilt_pair_where_a_client_reads_it()
    {
        Harness harness = JobFor("sprite.webp", "chapters.vtt");

        await harness.Job.Handle();

        // The cue file, not only the sheet: both clients resolve the scrub
        // preview from the thumbnails entry alone.
        harness.Files.Verify(
            f =>
                f.RepointPreviewTracksAsync(
                    HostFolder,
                    "thumbs_320x180.webp",
                    "thumbs_320x180.vtt",
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Fact]
    public async Task Leaves_a_folder_that_already_carries_a_current_sheet_alone()
    {
        Harness harness = JobFor("thumbs_320x180.webp", "thumbs_320x180.vtt", "sprite.webp");

        await harness.Job.Handle();

        harness.Refresher.Verify(
            r =>
                r.RefreshAsync(
                    It.IsAny<IStorage>(),
                    It.IsAny<string>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<Action<EncodingProgress>?>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Never,
            "re-rendering a title that already has a current preview would run "
                + "again on every scan, forever"
        );
    }
}
