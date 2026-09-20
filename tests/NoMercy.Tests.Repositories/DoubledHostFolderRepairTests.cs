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
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NoMercy.Data.Services;
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Music;
using NoMercy.MediaProcessing.AudioAnalysis;
using NoMercy.NmSystem.Domain;
using NoMercy.Storage;

namespace NoMercy.Tests.Repositories;

// The rows this sweep rewrites are the ones an older importer wrote with the
// album folder inside HostFolder twice: the folder with forward slashes, a
// separator, then the same folder with backslashes. Filename stays the bare
// file name, so every consumer that combines the two builds a path that exists
// nowhere and the track is unplayable and unanalysable. A rescan does not
// repair it, and the halves are only safe to collapse when the file is really
// where the repaired folder says it is.
[Trait("Category", "Unit")]
public class DoubledHostFolderRepairTests : IDisposable
{
    private const string AlbumFolder = "Q:/Music/Nine Vaults/Paper Lanterns";
    private const string DoubledAlbumFolder =
        @"Q:/Music/Nine Vaults/Paper Lanterns\Q:\Music\Nine Vaults\Paper Lanterns";
    private const string TrackFile = "/03. Paper Lanterns.flac";
    private const string LinuxAlbumFolder = "/mnt/vault/music/Nine Vaults/Paper Lanterns";
    private const string DoubledLinuxAlbumFolder = LinuxAlbumFolder + LinuxAlbumFolder;
    private const int AnalyzerVersion = 3;

    private static readonly Ulid DriverId = Ulid.NewUlid();
    private static readonly Ulid FolderId = Ulid.NewUlid();
    private static readonly Ulid LibraryId = Ulid.NewUlid();

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MediaContext> _options;

    public DoubledHostFolderRepairTests()
    {
        _connection = new("Data Source=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<MediaContext>().UseSqlite(_connection).Options;

        using MediaContext ctx = new(_options);
        ctx.Database.EnsureCreated();

        ctx.Drivers.Add(
            new()
            {
                Id = DriverId,
                Name = "Media",
                Type = "local",
            }
        );
        ctx.Folders.Add(
            new()
            {
                Id = FolderId,
                DriverId = DriverId,
                Path = "Libraries/Music",
            }
        );
        ctx.Libraries.Add(
            new()
            {
                Id = LibraryId,
                Title = "Music",
                Type = MediaTypes.MusicMediaType,
                AnalyzeAudio = true,
            }
        );
        ctx.SaveChanges();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// The driver the sweep asks "is the file there?": it combines with a
    /// single '/' and says yes only for the paths named.
    /// </summary>
    private static Mock<IStorageDriver> Driver(params string[] existingPaths)
    {
        Mock<IStorageDriver> driver = new();
        driver
            .Setup(d => d.CombinePath(It.IsAny<string>(), It.IsAny<string[]>()))
            .Returns<string, string[]>(
                (parent, segments) =>
                    parent.TrimEnd('/')
                    + string.Concat(segments.Select(segment => "/" + segment.TrimStart('/')))
            );
        driver.Setup(d => d.FileExists(It.IsAny<string>())).Returns<string>(existingPaths.Contains);
        return driver;
    }

    private DoubledHostFolderRepair BuildRepair(Mock<IStorageDriver> driver) =>
        new(
            new TestDbContextFactory(_options),
            driver.Object,
            NullLogger<DoubledHostFolderRepair>.Instance
        );

    /// <summary>
    /// One track in the music library, optionally carrying the verdict the
    /// affected rows really have: Failed, at the analyzer version that is
    /// current, which every "needs analysis" check reads as an answer.
    /// </summary>
    private async Task<Guid> AddTrack(
        string? hostFolder,
        string? filename = TrackFile,
        AudioAnalysisState? verdict = null
    )
    {
        Guid trackId = Guid.NewGuid();

        await using MediaContext ctx = new(_options);
        ctx.Tracks.Add(
            new()
            {
                Id = trackId,
                Name = "Paper Lanterns",
                HostFolder = hostFolder,
                Filename = filename,
                FolderId = FolderId,
                Duration = "03:12",
            }
        );
        ctx.LibraryTrack.Add(new(LibraryId, trackId));

        if (verdict is not null)
            ctx.TrackAudioAnalysis.Add(
                new()
                {
                    TrackId = trackId,
                    AnalyzerVersion = AnalyzerVersion,
                    State = verdict.Value,
                    FailureReason = "no file at the doubled path",
                    AnalyzedAt = DateTime.UtcNow,
                }
            );

        await ctx.SaveChangesAsync();
        return trackId;
    }

    private async Task<string?> ReadHostFolder()
    {
        await using MediaContext ctx = new(_options);
        return (await ctx.Tracks.AsNoTracking().SingleAsync()).HostFolder;
    }

    private async Task<List<Guid>> TracksNeedingAnalysis()
    {
        await using MediaContext ctx = new(_options);
        return await AudioAnalysisQueries
            .TracksNeedingAnalysis(ctx, [LibraryId], AnalyzerVersion)
            .ToListAsync();
    }

    [Fact]
    public async Task Collapses_a_doubled_host_folder_when_the_file_is_there()
    {
        await AddTrack(DoubledAlbumFolder);

        int repaired = await BuildRepair(Driver(AlbumFolder + TrackFile))
            .RunAsync(CancellationToken.None);

        repaired.Should().Be(1);
        (await ReadHostFolder()).Should().Be(AlbumFolder);
    }

    /// <summary>
    /// Nothing at the repaired path means the guess is unproven, and a row
    /// pointing at a path nobody checked is worse than the broken value: the
    /// broken one is at least recognisable.
    /// </summary>
    [Fact]
    public async Task Leaves_a_doubled_host_folder_alone_when_the_file_is_not_there()
    {
        await AddTrack(DoubledAlbumFolder);

        int repaired = await BuildRepair(Driver()).RunAsync(CancellationToken.None);

        repaired.Should().Be(0);
        (await ReadHostFolder()).Should().Be(PathAsStored(DoubledAlbumFolder));
    }

    /// <summary>
    /// Two roots naming different folders is a value only a human can judge.
    /// </summary>
    [Fact]
    public async Task Leaves_a_second_root_alone_when_it_is_not_a_copy_of_the_first()
    {
        const string mismatched = @"Q:/Music/Nine Vaults/Paper Lanterns\R:\Archive\Nine Vaults";
        await AddTrack(mismatched);

        int repaired = await BuildRepair(Driver(AlbumFolder + TrackFile))
            .RunAsync(CancellationToken.None);

        repaired.Should().Be(0);
        (await ReadHostFolder()).Should().Be(PathAsStored(mismatched));
    }

    [Theory]
    [InlineData(AlbumFolder)]
    [InlineData("//vault-01/music/Nine Vaults/Paper Lanterns")]
    [InlineData("music/Nine Vaults/Paper Lanterns")]
    public async Task Leaves_a_healthy_host_folder_alone(string hostFolder)
    {
        await AddTrack(hostFolder);

        int repaired = await BuildRepair(Driver(AlbumFolder + TrackFile))
            .RunAsync(CancellationToken.None);

        repaired.Should().Be(0);
        (await ReadHostFolder()).Should().Be(PathAsStored(hostFolder));
    }

    /// <summary>
    /// A repaired row carries no second root any more, so the next boot's sweep
    /// must not select it and must not touch the value again.
    /// </summary>
    [Fact]
    public async Task Is_idempotent()
    {
        await AddTrack(DoubledAlbumFolder);
        Mock<IStorageDriver> driver = Driver(AlbumFolder + TrackFile);

        (await BuildRepair(driver).RunAsync(CancellationToken.None)).Should().Be(1);
        (await BuildRepair(driver).RunAsync(CancellationToken.None)).Should().Be(0);

        (await ReadHostFolder()).Should().Be(AlbumFolder);
    }

    /// <summary>
    /// On Linux the doubling carries no marker at all — no drive letter, no
    /// doubled separator — so the shape is found only by splitting and comparing.
    /// </summary>
    [Fact]
    public async Task Collapses_a_repeated_linux_folder_when_the_file_is_there()
    {
        await AddTrack(DoubledLinuxAlbumFolder);

        int repaired = await BuildRepair(Driver(LinuxAlbumFolder + TrackFile))
            .RunAsync(CancellationToken.None);

        repaired.Should().Be(1);
        (await ReadHostFolder()).Should().Be(LinuxAlbumFolder);
    }

    [Theory]
    [InlineData(LinuxAlbumFolder)]
    [InlineData("/mnt/vault/music/mnt/vault/photos")]
    [InlineData("/music/Nine Vaults/music/Nine Vaults Live")]
    public async Task Leaves_a_linux_folder_that_is_not_doubled_alone(string hostFolder)
    {
        await AddTrack(hostFolder);

        int repaired = await BuildRepair(Driver(LinuxAlbumFolder + TrackFile))
            .RunAsync(CancellationToken.None);

        repaired.Should().Be(0);
        (await ReadHostFolder()).Should().Be(hostFolder);
    }

    /// <summary>
    /// A folder that merely looks repeated — a library really mounted at
    /// <c>/music/music</c> — resolves as it stands, and a sweep that "repaired"
    /// it would point a playable row at a different file.
    /// </summary>
    [Fact]
    public async Task Leaves_a_folder_alone_when_the_stored_path_already_resolves()
    {
        await AddTrack("/music/music");

        int repaired = await BuildRepair(Driver("/music/music" + TrackFile, "/music" + TrackFile))
            .RunAsync(CancellationToken.None);

        repaired.Should().Be(0);
        (await ReadHostFolder()).Should().Be("/music/music");
    }

    /// <summary>
    /// The affected tracks already carry a Failed verdict at the current
    /// analyzer version, which both the sweep query and the job's own check read
    /// as an answer. Without the reset the row would be correct and still never
    /// analysed — the bug the owner reported.
    /// </summary>
    [Fact]
    public async Task Resets_the_verdict_of_a_repaired_track_so_it_is_analysed_again()
    {
        Guid trackId = await AddTrack(DoubledAlbumFolder, verdict: AudioAnalysisState.Failed);

        (await TracksNeedingAnalysis()).Should().BeEmpty();

        await BuildRepair(Driver(AlbumFolder + TrackFile)).RunAsync(CancellationToken.None);

        (await TracksNeedingAnalysis()).Should().ContainSingle().Which.Should().Be(trackId);

        await using MediaContext ctx = new(_options);
        TrackAudioAnalysis verdict = await ctx.TrackAudioAnalysis.AsNoTracking().SingleAsync();
        verdict.State.Should().Be(AudioAnalysisState.Pending);
        verdict.FailureReason.Should().BeNull();
    }

    /// <summary>
    /// A verdict is an answer about a file the row really addresses; only a row
    /// this sweep moved has reason to be measured again.
    /// </summary>
    [Fact]
    public async Task Leaves_the_verdict_of_a_track_it_did_not_repair_alone()
    {
        await AddTrack(AlbumFolder, verdict: AudioAnalysisState.Failed);

        await BuildRepair(Driver(AlbumFolder + TrackFile)).RunAsync(CancellationToken.None);

        await using MediaContext ctx = new(_options);
        TrackAudioAnalysis verdict = await ctx.TrackAudioAnalysis.AsNoTracking().SingleAsync();
        verdict.State.Should().Be(AudioAnalysisState.Failed);
    }

    /// <summary>
    /// A driver that throws on one path — a share that went away, a permission
    /// it lacks — must cost that row and no other: the sweep runs on boot, and
    /// one unreachable mount cannot be allowed to keep every other row broken.
    /// </summary>
    [Fact]
    public async Task Keeps_sweeping_when_the_driver_throws_on_one_row()
    {
        await AddTrack(DoubledLinuxAlbumFolder);
        await AddTrack(DoubledAlbumFolder);

        Mock<IStorageDriver> driver = Driver(AlbumFolder + TrackFile);
        driver
            .Setup(d => d.FileExists(It.Is<string>(path => path.StartsWith("/mnt/vault"))))
            .Throws(new IOException("the mount is gone"));

        int repaired = await BuildRepair(driver).RunAsync(CancellationToken.None);

        repaired.Should().Be(1);

        await using MediaContext ctx = new(_options);
        List<string?> stored = await ctx
            .Tracks.AsNoTracking()
            .Select(track => track.HostFolder)
            .ToListAsync();

        stored.Should().Contain(AlbumFolder);
        stored.Should().Contain(DoubledLinuxAlbumFolder);
    }

    /// <summary>
    /// A shutdown during the sweep stops it between rows, and what was decided
    /// before the stop is written all the same: the sweep runs on every boot,
    /// and a server that is restarted twice must not lose the same repairs
    /// twice.
    /// </summary>
    [Fact]
    public async Task Writes_the_repairs_decided_before_a_cancellation()
    {
        await AddTrack(DoubledAlbumFolder);
        await AddTrack(DoubledAlbumFolder);

        using CancellationTokenSource cancellation = new();
        Mock<IStorageDriver> driver = Driver(AlbumFolder + TrackFile);
        driver
            .Setup(d => d.FileExists(It.IsAny<string>()))
            .Returns<string>(path =>
            {
                // The first row asks the driver; cancel while it is being
                // decided, so the second row is never reached.
                cancellation.Cancel();
                return path == AlbumFolder + TrackFile;
            });

        int repaired = await BuildRepair(driver).RunAsync(cancellation.Token);

        repaired.Should().Be(1);

        await using MediaContext ctx = new(_options);
        List<string?> stored = await ctx
            .Tracks.AsNoTracking()
            .Select(track => track.HostFolder)
            .ToListAsync();

        stored.Should().BeEquivalentTo([AlbumFolder, PathAsStored(DoubledAlbumFolder)]);
    }

    // Track.HostFolder normalises separators on the way in, so an untouched row
    // reads back with forward slashes whatever it was written with. The test
    // asserts "unchanged", not "byte-identical to the literal above".
    private static string PathAsStored(string hostFolder) => hostFolder.Replace('\\', '/');

    private sealed class TestDbContextFactory(DbContextOptions<MediaContext> options)
        : IDbContextFactory<MediaContext>
    {
        public MediaContext CreateDbContext() => new(options);
    }
}
