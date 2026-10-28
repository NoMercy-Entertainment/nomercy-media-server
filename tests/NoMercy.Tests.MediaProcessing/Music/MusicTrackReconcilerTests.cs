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
using Microsoft.Extensions.Logging;
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

    private Guid Seed(
        string absoluteFile,
        DateTime? missingSince = null,
        Guid? id = null,
        int trackNumber = 0,
        string duration = "",
        Ulid? folderId = null
    )
    {
        string normalized = absoluteFile.Replace('\\', '/');
        int slash = normalized.LastIndexOf('/');
        Track row = new()
        {
            Id = id ?? Guid.NewGuid(),
            Name = normalized[(slash + 1)..],
            FolderId = folderId ?? _folder.Id,
            HostFolder = normalized[..slash],
            Filename = normalized[slash..],
            TrackNumber = trackNumber,
            Duration = duration,
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
        IRecordingIdentifier? identifier = null,
        ILogger? logger = null,
        ITrackFileReader? reader = null
    ) => new(_context, logger ?? NullLogger.Instance, identifier, cache ?? new(), reader);

    private sealed class FakeTrackFileReader(Dictionary<string, TrackFileFacts> facts)
        : ITrackFileReader
    {
        public Task<TrackFileFacts?> ReadAsync(
            IStorageDriver driver,
            string path,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                facts.TryGetValue(System.IO.Path.GetFileName(path), out TrackFileFacts? found)
                    ? found
                    : null
            );
    }

    private static TrackFileFacts Facts(int track, int seconds) =>
        new(track, TimeSpan.FromSeconds(seconds));

    // ExecuteUpdate skips the Track setters, so this stores a path the way an old row has it.
    private void StoreRawHostFolder(Guid id, string hostFolder) =>
        _context
            .Tracks.Where(track => track.Id == id)
            .ExecuteUpdate(setters => setters.SetProperty(track => track.HostFolder, hostFolder));

    private sealed class ListLogger : ILogger
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

    private Task<MusicReconcileResult> Reconcile(
        MusicTrackReconciler reconciler,
        DateTime? now = null
    ) => reconciler.ReconcileAsync(_folder.Id, _folder.Path, _driver, nowUtc: now ?? Now);

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
        _context
            .Tracks.AsNoTracking()
            .Select(t => t.Id)
            .ToList()
            .Should()
            .Contain(recent)
            .And.NotContain(expired);
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
    public async Task A_forced_pass_lists_a_folder_again_that_an_automatic_pass_would_reuse()
    {
        DateTime written = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Mock<IStorageDriver> driver = new();
        driver.Setup(d => d.DirectoryExists("/m")).Returns(true);
        driver.Setup(d => d.GetLastWriteTimeUtc("/m")).Returns(written);
        driver
            .Setup(d => d.EnumerateEntries("/m", "*", SearchOption.TopDirectoryOnly))
            .Returns([new StorageEntryInfo("/m/one.flac", false, 10, written)]);
        MusicListingCache cache = new();
        MusicTrackReconciler reconciler = Reconciler(cache);

        await reconciler.ReconcileAsync(_folder.Id, "/m", driver.Object, nowUtc: Now);
        await reconciler.ReconcileAsync(_folder.Id, "/m", driver.Object, nowUtc: Now);
        driver.Verify(
            d => d.EnumerateEntries("/m", "*", SearchOption.TopDirectoryOnly),
            Times.Once
        );

        await reconciler.ReconcileAsync(_folder.Id, "/m", driver.Object, nowUtc: Now, force: true);

        driver.Verify(
            d => d.EnumerateEntries("/m", "*", SearchOption.TopDirectoryOnly),
            Times.Exactly(2)
        );
    }

    private Mock<IStorageDriver> DriverWithUnreadableFolder()
    {
        DateTime written = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Mock<IStorageDriver> driver = new();
        driver.Setup(d => d.DirectoryExists("/m")).Returns(true);
        driver.Setup(d => d.GetLastWriteTimeUtc("/m")).Returns(written);
        driver.Setup(d => d.GetLastWriteTimeUtc("/m/Good")).Returns(written);
        driver
            .Setup(d => d.GetLastWriteTimeUtc("/m/Bad"))
            .Throws(
                new IOException(
                    "The filename, directory name, or volume label syntax is incorrect."
                )
            );
        driver
            .Setup(d => d.EnumerateEntries("/m", "*", SearchOption.TopDirectoryOnly))
            .Returns([
                new StorageEntryInfo("/m/Good", true, 0, written),
                new StorageEntryInfo("/m/Bad", true, 0, written),
            ]);
        driver
            .Setup(d => d.EnumerateEntries("/m/Good", "*", SearchOption.TopDirectoryOnly))
            .Returns([new StorageEntryInfo("/m/Good/one.flac", false, 10, written)]);
        driver.Setup(d => d.FileExists("/m/Good/one.flac")).Returns(true);
        return driver;
    }

    [Fact]
    public async Task A_folder_that_cannot_be_read_holds_its_tracks_and_the_pass_goes_on()
    {
        Seed("/m/Good/one.flac");
        Guid vanished = Seed("/m/Good/gone.flac");
        Guid held = Seed("/m/Bad/two.flac");
        Guid heldExpired = Seed("/m/Bad/three.flac", Now.AddDays(-90));
        ListLogger logger = new();

        MusicReconcileResult result = await Reconciler(logger: logger)
            .ReconcileAsync(_folder.Id, "/m", DriverWithUnreadableFolder().Object, nowUtc: Now);

        result.Marked.Should().Be(1);
        result.Removed.Should().Be(0);
        Row(vanished).MissingSince.Should().Be(Now);
        Row(held).MissingSince.Should().BeNull();
        Row(heldExpired).Should().NotBeNull();
        logger
            .Entries.Count(entry =>
                entry.Level == LogLevel.Warning && entry.Message.Contains("'/m/Bad'")
            )
            .Should()
            .Be(1);
    }

    [Fact]
    public async Task A_row_stored_with_mixed_separators_is_the_same_file_as_its_listing()
    {
        string original = Write("01 Song.flac");
        Guid id = Seed(original);
        Write("02 Other.flac");
        string normalizedRoot = _root.Replace('\\', '/');
        string mixedHost = _root.Replace('/', '\\') + "/Synthetic Artist/Album";
        StoreRawHostFolder(id, mixedHost);
        MusicListingCache cache = new();
        await Reconcile(Reconciler(cache));

        string target = Path.Combine(_root, "Synthetic Artist", "Album Moved");
        Directory.CreateDirectory(target);
        File.Move(original, Path.Combine(target, "01 Song.flac"));
        MusicReconcileResult result = await Reconcile(Reconciler(cache));

        result.Moved.Should().Be(1);
        Row(id).HostFolder.Should().Be($"{normalizedRoot}/Synthetic Artist/Album Moved");
        _context.Tracks.AsNoTracking().Count(track => track.HostFolder == mixedHost).Should().Be(0);
    }

    [Fact]
    public async Task A_pass_logs_one_summary_line_with_every_count()
    {
        Write("01 Live.flac");
        Seed(Path.Combine(AlbumFolder, "02 Gone.flac"));
        ListLogger logger = new();

        await Reconcile(Reconciler(logger: logger));

        logger
            .Entries.Where(entry => entry.Message.Contains("folder(s) listed"))
            .Select(entry => entry.Message)
            .Should()
            .ContainSingle()
            .Which.Should()
            .EndWith(
                "3 folder(s) listed, 0 skipped, 0 repointed, 1 marked unavailable, 0 cleared, 0 removed, 0 guard skip(s)"
            );
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
            .Setup(i =>
                i.IdentifyAsync(
                    It.IsAny<IStorageDriver>(),
                    It.Is<string>(p => p.EndsWith("01 Renamed.flac")),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync([id]);

        MusicReconcileResult result = await Reconcile(Reconciler(identifier: identifier.Object));

        result.Moved.Should().Be(1);
        Row(id).Filename.Should().Be("/01 Renamed.flac");
    }

    private const string OldName = "01 Song [Artist One feat. Artist Two].flac";
    private const string NewName = "01 Song [Artist One, Artist Two].flac";

    // A hidden row (3:45, track 1) whose file was renamed in place; two live rows stay.
    private Guid HiddenRowWithRenamedFile(string newName = NewName)
    {
        Seed(Write("02 Other.flac"));
        Seed(Write("03 Third.flac"));
        string original = Write(OldName);
        Guid id = Seed(original, Now.AddDays(-3), trackNumber: 1, duration: "03:45");
        File.Move(original, Path.Combine(AlbumFolder, newName));
        return id;
    }

    private Task<MusicReconcileResult> ReconcileWith(
        Dictionary<string, TrackFileFacts> facts,
        ILogger? logger = null
    ) => Reconcile(Reconciler(logger: logger, reader: new FakeTrackFileReader(facts)));

    [Fact]
    public async Task A_renamed_file_with_the_same_track_number_and_length_is_linked_to_the_hidden_row()
    {
        Guid id = HiddenRowWithRenamedFile();
        ListLogger logger = new();

        MusicReconcileResult result = await ReconcileWith(
            new() { [NewName] = Facts(1, 227) },
            logger
        );

        result.Moved.Should().Be(1);
        Track row = Row(id);
        row.Filename.Should().Be("/" + NewName);
        row.MissingSince.Should().BeNull();
        logger
            .Entries.Count(entry =>
                entry.Level == LogLevel.Warning && entry.Message.Contains(NewName)
            )
            .Should()
            .Be(1);
    }

    [Fact]
    public async Task A_renamed_file_whose_length_differs_by_more_than_two_seconds_stays_unlinked()
    {
        Guid id = HiddenRowWithRenamedFile();

        MusicReconcileResult result = await ReconcileWith(new() { [NewName] = Facts(1, 230) });

        result.Moved.Should().Be(0);
        Row(id).Filename.Should().Be("/" + OldName);
        Row(id).MissingSince.Should().NotBeNull();
    }

    [Fact]
    public async Task A_renamed_file_with_another_track_number_stays_unlinked()
    {
        Guid id = HiddenRowWithRenamedFile();

        MusicReconcileResult result = await ReconcileWith(new() { [NewName] = Facts(2, 225) });

        result.Moved.Should().Be(0);
        Row(id).Filename.Should().Be("/" + OldName);
    }

    [Fact]
    public async Task Two_new_files_that_both_fit_the_hidden_row_link_neither()
    {
        Guid id = HiddenRowWithRenamedFile();
        Write("01 Song [Artist One and Artist Two].flac");

        MusicReconcileResult result = await ReconcileWith(
            new()
            {
                [NewName] = Facts(1, 225),
                ["01 Song [Artist One and Artist Two].flac"] = Facts(1, 226),
            }
        );

        result.Moved.Should().Be(0);
        Row(id).Filename.Should().Be("/" + OldName);
    }

    [Fact]
    public async Task A_new_file_another_row_owns_is_not_linked_to_the_hidden_row()
    {
        Guid id = HiddenRowWithRenamedFile();
        Seed(Path.Combine(AlbumFolder, NewName), folderId: Ulid.NewUlid());

        MusicReconcileResult result = await ReconcileWith(new() { [NewName] = Facts(1, 225) });

        result.Moved.Should().Be(0);
        Row(id).Filename.Should().Be("/" + OldName);
    }

    [Fact]
    public async Task A_new_file_in_another_folder_is_not_linked_to_the_hidden_row()
    {
        Guid id = HiddenRowWithRenamedFile();
        File.Delete(Path.Combine(AlbumFolder, NewName));
        Write(NewName, Path.Combine(_root, "Synthetic Artist", "Other Album"));

        MusicReconcileResult result = await ReconcileWith(new() { [NewName] = Facts(1, 225) });

        result.Moved.Should().Be(0);
        Row(id).Filename.Should().Be("/" + OldName);
    }

    [Fact]
    public async Task A_new_file_whose_tags_cannot_be_read_stays_unlinked()
    {
        Guid id = HiddenRowWithRenamedFile();

        MusicReconcileResult result = await ReconcileWith([]);

        result.Moved.Should().Be(0);
        Row(id).Filename.Should().Be("/" + OldName);
    }
}
