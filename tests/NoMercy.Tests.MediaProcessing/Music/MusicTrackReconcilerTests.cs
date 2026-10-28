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
using NoMercy.Database.Models.Music;
using NoMercy.MediaProcessing.Music;
using NoMercy.Storage;
using NoMercy.Storage.Drivers.Local;

namespace NoMercy.Tests.MediaProcessing.Music;

/// <summary>
/// Files that vanish, move or return on disk reach the Track rows: a vanished file is marked
/// unavailable, removed only after the grace period; a moved file is repointed; nothing is
/// marked when the root is unreadable or too many files vanish at once.
/// </summary>
[Trait("Category", "Unit")]
public sealed class MusicTrackReconcilerTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);

    private readonly string _root;
    private readonly SqliteConnection _connection;
    private readonly MediaContext _context;
    private readonly Folder _folder;
    private readonly LocalStorageDriver _driver = new();

    public MusicTrackReconcilerTests()
    {
        _root = Path.Combine(
            "C:/Projects/.work/tmp".Replace('/', Path.DirectorySeparatorChar),
            $"nm-reconcile-{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(AlbumFolder);

        _connection = new("Data Source=:memory:");
        _connection.Open();
        using (SqliteCommand foreignKeys = _connection.CreateCommand())
        {
            foreignKeys.CommandText = "PRAGMA foreign_keys = OFF;";
            foreignKeys.ExecuteNonQuery();
        }

        _context = new(new DbContextOptionsBuilder<MediaContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();
        _folder = new()
        {
            Id = Ulid.NewUlid(),
            DriverId = Ulid.NewUlid(),
            Path = _root.Replace('\\', '/'),
        };
        _context.Folders.Add(_folder);
        _context.SaveChanges();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private string AlbumFolder => Path.Combine(_root, "Synthetic Artist", "Album");

    private string Write(string name, string folder = "")
    {
        string directory = folder.Length == 0 ? AlbumFolder : folder;
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name);
        File.WriteAllText(path, name);
        return path.Replace('\\', '/');
    }

    private Guid Seed(string absoluteFile, DateTime? missingSince = null, Guid? id = null)
    {
        string normalized = absoluteFile.Replace('\\', '/');
        int slash = normalized.LastIndexOf('/');
        Track row = new()
        {
            Id = id ?? Guid.NewGuid(),
            Name = normalized[(slash + 1)..],
            FolderId = _folder.Id,
            HostFolder = normalized[..slash],
            Filename = normalized[slash..],
            MissingSince = missingSince,
            LibraryFolder = null!,
            Metadata = null!,
        };
        _context.Tracks.Add(row);
        _context.SaveChanges();
        return row.Id;
    }

    private MusicTrackReconciler Reconciler(
        MusicListingCache? cache = null,
        IRecordingIdentifier? identifier = null
    ) => new(_context, NullLogger.Instance, identifier, cache ?? new());

    private Task<MusicReconcileResult> Reconcile(MusicTrackReconciler reconciler, DateTime? now = null) =>
        reconciler.ReconcileAsync(
            _folder.Id,
            _folder.Path,
            _driver,
            nowUtc: now ?? Now
        );

    private Track Row(Guid id) => _context.Tracks.AsNoTracking().Single(track => track.Id == id);

    [Fact]
    public async Task A_vanished_file_is_marked_unavailable_and_its_row_stays()
    {
        Seed(Write("01 Live.flac"));
        Guid gone = Seed(Path.Combine(AlbumFolder, "02 Gone.flac"));

        MusicReconcileResult result = await Reconcile(Reconciler());

        result.Marked.Should().Be(1);
        Row(gone).MissingSince.Should().Be(Now);
    }

    [Fact]
    public async Task A_file_that_comes_back_clears_the_mark()
    {
        Guid back = Seed(Write("01 Back.flac"), missingSince: Now.AddDays(-3));

        MusicReconcileResult result = await Reconcile(Reconciler());

        result.Cleared.Should().Be(1);
        Row(back).MissingSince.Should().BeNull();
    }

    [Fact]
    public async Task A_missing_row_is_removed_only_after_thirty_days()
    {
        Write("01 Live.flac");
        Guid recent = Seed(Path.Combine(AlbumFolder, "02 Recent.flac"), Now.AddDays(-29));
        Guid expired = Seed(Path.Combine(AlbumFolder, "03 Expired.flac"), Now.AddDays(-31));

        MusicReconcileResult result = await Reconcile(Reconciler());

        result.Removed.Should().Be(1);
        _context.Tracks.AsNoTracking().Select(t => t.Id).ToList().Should().Contain(recent).And.NotContain(expired);
    }

    [Fact]
    public async Task An_expired_row_a_playlist_holds_is_kept()
    {
        Write("01 Live.flac");
        Guid held = Seed(Path.Combine(AlbumFolder, "02 Held.flac"), Now.AddDays(-90));
        _context.PlaylistTrack.Add(new(Guid.NewGuid(), held));
        await _context.SaveChangesAsync();

        await Reconcile(Reconciler());

        Row(held).Should().NotBeNull();
    }

    [Fact]
    public async Task A_root_that_is_not_there_marks_nothing()
    {
        Guid row = Seed(Path.Combine(AlbumFolder, "01 Unmounted.flac"));
        Directory.Delete(_root, recursive: true);

        MusicReconcileResult result = await Reconcile(Reconciler());

        result.DeletesSkipped.Should().BeTrue();
        Row(row).MissingSince.Should().BeNull();
    }

    [Fact]
    public async Task A_root_that_lists_empty_marks_nothing()
    {
        Guid row = Seed(Path.Combine(AlbumFolder, "01 Unlisted.flac"));

        MusicReconcileResult result = await Reconcile(Reconciler());

        result.DeletesSkipped.Should().BeTrue();
        Row(row).MissingSince.Should().BeNull();
    }

    [Fact]
    public async Task Too_many_files_vanishing_at_once_marks_nothing()
    {
        for (int index = 0; index < 3; index++)
            Seed(Write($"{index:00} Live.flac"));
        List<Guid> vanished = [];
        for (int index = 0; index < 17; index++)
            vanished.Add(Seed(Path.Combine(AlbumFolder, $"{index:00} Gone.flac")));

        MusicReconcileResult result = await Reconcile(Reconciler());

        result.DeletesSkipped.Should().BeTrue();
        _context.Tracks.AsNoTracking().Count(track => track.MissingSince != null).Should().Be(0);
    }

    [Fact]
    public async Task A_moved_file_with_the_same_size_and_time_is_repointed_on_the_next_pass()
    {
        string original = Write("01 Song.flac");
        Guid id = Seed(original);
        Write("02 Other.flac");
        MusicListingCache cache = new();
        await Reconcile(Reconciler(cache));

        string target = Path.Combine(_root, "Synthetic Artist", "Album Moved");
        Directory.CreateDirectory(target);
        File.Move(original, Path.Combine(target, "01 Song.flac"));

        MusicReconcileResult result = await Reconcile(Reconciler(cache));

        result.Moved.Should().Be(1);
        Track row = Row(id);
        row.HostFolder.Should().EndWith("/Album Moved");
        row.MissingSince.Should().BeNull();
    }

    [Fact]
    public async Task A_moved_file_the_listing_cannot_match_is_repointed_by_its_content()
    {
        string original = Write("01 Song.flac");
        Guid id = Seed(original);
        Write("02 Other.flac");
        string target = Path.Combine(_root, "Synthetic Artist", "Album Moved");
        Directory.CreateDirectory(target);
        File.Move(original, Path.Combine(target, "01 Renamed.flac"));

        Mock<IRecordingIdentifier> identifier = new();
        identifier
            .Setup(i => i.IdentifyAsync(It.IsAny<IStorageDriver>(), It.Is<string>(p => p.EndsWith("01 Renamed.flac")), It.IsAny<CancellationToken>()))
            .ReturnsAsync([id]);

        MusicReconcileResult result = await Reconcile(Reconciler(identifier: identifier.Object));

        result.Moved.Should().Be(1);
        Row(id).Filename.Should().Be("/01 Renamed.flac");
    }
}
