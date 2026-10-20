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
using NoMercy.Data.Services;
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;
using NoMercy.NmSystem.Dto;
using NoMercy.Providers.AcoustId;
using NoMercy.Storage;

namespace NoMercy.Tests.MediaProcessing.Music;

/// <summary>
/// A music file whose probe failed is skipped, not identified. Matching it
/// against a release with an empty probe stored a track with no duration and
/// a quality of zero, and that row then counted as imported (#320).
/// </summary>
[Trait("Category", "Unit")]
public sealed class MusicLogicFailedProbeTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MediaContext> _options;

    public MusicLogicFailedProbeTests()
    {
        _connection = new("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<MediaContext>().UseSqlite(_connection).Options;

        using MediaContext context = new(_options);
        context.Database.EnsureCreated();
    }

    [Fact]
    public async Task A_file_whose_probe_failed_is_not_fingerprinted_or_matched()
    {
        // Missing on disk: ffprobe cannot open it, so the probe fails whether
        // the binary is present or not.
        string missing = Path.Combine(
            Path.GetTempPath(),
            $"nm-missing-{Guid.NewGuid():N}",
            "01 - A Track.flac"
        );
        MediaFile file = new()
        {
            Name = Path.GetFileName(missing),
            Path = missing,
            Parsed = new() { Title = "A Track", FilePath = missing },
        };
        MediaFolderExtend folder = new()
        {
            Name = "An Album",
            Path = Path.GetDirectoryName(missing)!,
            Files = [file],
        };

        Mock<IAudioFingerprinter> fingerprinter = new();
        Mock<IDbContextFactory<MediaContext>> contextFactory = new();
        contextFactory
            .Setup(factory => factory.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MediaContext(_options));

        await using MusicLogic logic = new(
            NullLogger<MusicLogic>.Instance,
            new Library(),
            folder,
            contextFactory.Object,
            new Mock<IStorageFactory>().Object,
            fingerprinter.Object
        );

        await logic.Process();

        fingerprinter.VerifyNoOtherCalls();
    }

    public void Dispose()
    {
        _connection.Dispose();
    }
}
