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

using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Music;
using NoMercy.Database.Models.Storage;
using NoMercy.MediaProcessing.DerivedAudio;
using NoMercy.Storage;
using NoMercy.Storage.Drivers.Local;
using NoMercy.Storage.Validation;

namespace NoMercy.Tests.MediaProcessing.DerivedAudio;

// This test namespace shares its last segment with NoMercy.Database.Models.Music.DerivedAudio,
// but this file only ever reaches that type through NoMercy.Database.MediaContext.DerivedAudio
// (the DbSet), never the bare type name, so no alias is needed here.
[Trait("Category", "Unit")]
public sealed class DerivedAudioStoreTests : IDisposable
{
    private readonly string _root;
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MediaContext> _options;
    private readonly IDbContextFactory<MediaContext> _contextFactory;

    public DerivedAudioStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"nm-derived-audio-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);

        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<MediaContext>().UseSqlite(_connection).Options;
        using (MediaContext context = new(_options))
        {
            context.Database.EnsureCreated();
        }

        Mock<IDbContextFactory<MediaContext>> factory = new();
        factory
            .Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MediaContext(_options));
        factory.Setup(f => f.CreateDbContext()).Returns(() => new MediaContext(_options));
        _contextFactory = factory.Object;
    }

    public void Dispose()
    {
        _connection.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup; a locked handle on Windows must not fail the test run.
        }
        GC.SuppressFinalize(this);
    }

    // Track.FolderId is a required FK to Folder, itself a required FK to
    // Driver, and this test class runs with foreign keys ON, so the whole
    // chain has to be inserted with the track. Mirrors
    // AnalysisRecordModelTests.TrackRow.
    private static Track TrackRow(Guid id)
    {
        Ulid folderId = Ulid.NewUlid();
        return new Track
        {
            Id = id,
            Name = "A Track",
            Duration = "03:45",
            FolderId = folderId,
            LibraryFolder = new Folder
            {
                Id = folderId,
                Path = "/music",
                DriverId = Driver.SystemLocalDriverId,
                Driver = new Driver
                {
                    Id = Driver.SystemLocalDriverId,
                    Name = "local",
                    Type = "local",
                },
            },
        };
    }

    private IDerivedAudioStore Store() => StoreWith(null);

    private DerivedAudioStore StoreWith(Func<Task>? beforeDelete) =>
        StoreWith(beforeDelete, null, null);

    private DerivedAudioStore StoreWith(
        Func<Task>? beforeDelete,
        Func<Task>? afterPreCheck,
        RecordingInterceptor? recorder
    )
    {
        LocalStorageDriver driver = new();
        StoragePathGuard guard = new([_root], driver);
        IStorage storage = new LocalStorage(driver, guard);
        return new DerivedAudioStore(
            storage,
            FactoryRecording(recorder),
            NullLogger<DerivedAudioStore>.Instance
        )
        {
            BeforeDelete = beforeDelete,
            AfterPreCheck = afterPreCheck,
        };
    }

    /// <summary>
    /// The shared factory, or one whose contexts report every statement they
    /// run to <paramref name="recorder" /> - the same in-memory database
    /// either way.
    /// </summary>
    private IDbContextFactory<MediaContext> FactoryRecording(RecordingInterceptor? recorder)
    {
        if (recorder is null)
        {
            return _contextFactory;
        }

        DbContextOptions<MediaContext> options = new DbContextOptionsBuilder<MediaContext>()
            .UseSqlite(_connection)
            .AddInterceptors(recorder)
            .Options;

        Mock<IDbContextFactory<MediaContext>> factory = new();
        factory
            .Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MediaContext(options));
        factory.Setup(f => f.CreateDbContext()).Returns(() => new MediaContext(options));
        return factory.Object;
    }

    /// <summary>
    /// Records the statements a store runs, because the difference the
    /// re-check under the key lock makes is otherwise invisible: an UPDATE
    /// against a row eviction has already deleted changes nothing a later
    /// read could see.
    /// </summary>
    private sealed class RecordingInterceptor : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result
        )
        {
            Commands.Add(command.CommandText);
            return base.NonQueryExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default
        )
        {
            Commands.Add(command.CommandText);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    [Fact]
    public async Task Put_StoresUnderItsSha256_AndRegistersTheRow()
    {
        byte[] content = Encoding.UTF8.GetBytes("stem bytes");
        string expectedKey = Convert.ToHexStringLower(SHA256.HashData(content));

        DerivedAudioEntry entry = await Store().PutAsync(new MemoryStream(content), "audio/opus");

        entry.Key.Should().Be(expectedKey);
        entry.Bytes.Should().Be(content.Length);
        (await Store().ExistsAsync(entry.Key)).Should().BeTrue();
        Store().RelativePath(entry.Key).Should().Be($"{expectedKey[..2]}/{expectedKey}");
        await using MediaContext read = new(_options);
        (await read.DerivedAudio.AsNoTracking().SingleAsync()).Bytes.Should().Be(content.Length);
    }

    [Fact]
    public async Task PutTwice_IsOneFileAndOneRow()
    {
        byte[] content = Encoding.UTF8.GetBytes("the same bytes, put twice");
        IDerivedAudioStore store = Store();

        DerivedAudioEntry first = await store.PutAsync(new MemoryStream(content), "audio/opus");
        DerivedAudioEntry second = await store.PutAsync(new MemoryStream(content), "audio/opus");

        second.Key.Should().Be(first.Key);
        await using MediaContext read = new(_options);
        (await read.DerivedAudio.AsNoTracking().CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task OpenRead_ReturnsTheBytes_AndBumpsLastUsedAt()
    {
        byte[] content = Encoding.UTF8.GetBytes("read this back");
        IDerivedAudioStore store = Store();
        DerivedAudioEntry entry = await store.PutAsync(new MemoryStream(content), "audio/opus");

        DateTime before;
        await using (MediaContext read = new(_options))
        {
            before = (await read.DerivedAudio.AsNoTracking().SingleAsync()).LastUsedAt;
        }

        await Task.Delay(20);

        await using Stream? stream = await store.OpenReadAsync(entry.Key);
        stream.Should().NotBeNull();
        using MemoryStream buffer = new();
        await stream!.CopyToAsync(buffer);
        buffer.ToArray().Should().Equal(content);

        await using MediaContext read2 = new(_options);
        DateTime after = (await read2.DerivedAudio.AsNoTracking().SingleAsync()).LastUsedAt;
        after.Should().BeAfter(before);
    }

    [Fact]
    public async Task OpenRead_OfAnUnknownKey_IsNull()
    {
        Stream? stream = await Store().OpenReadAsync(new string('a', 64));

        stream.Should().BeNull();
    }

    /// <summary>
    /// The pre-check that keeps an unknown key from minting a semaphore saw a
    /// register row, and eviction took the entry while this call was still
    /// waiting for the key lock. The touch must not run at all then - the row
    /// it would bump is gone, and the window between the pre-check and the
    /// lock is exactly what the re-check under the lock closes.
    /// </summary>
    [Fact]
    public async Task Touch_UnderTheLock_DoesNothingWhenEvictionWonTheRace()
    {
        IDerivedAudioStore evictor = Store();
        DerivedAudioEntry entry = await evictor.PutAsync(
            new MemoryStream(Encoding.UTF8.GetBytes("content eviction is about to take")),
            "audio/opus"
        );

        RecordingInterceptor recorder = new();
        DerivedAudioStore store = StoreWith(
            null,
            async () =>
            {
                await evictor.DeleteAsync(entry.Key);
                recorder.Commands.Clear();
            },
            recorder
        );

        await store.TouchAsync(entry.Key);

        recorder
            .Commands.Should()
            .NotContain(command => command.Contains("UPDATE", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The same race one member along: the file the reader was about to open
    /// went with the row, so the caller is told there is nothing rather than
    /// handed the <see cref="FileNotFoundException" /> an open of a deleted
    /// path throws.
    /// </summary>
    [Fact]
    public async Task OpenRead_UnderTheLock_ReturnsNullWhenEvictionWonTheRace()
    {
        IDerivedAudioStore evictor = Store();
        DerivedAudioEntry entry = await evictor.PutAsync(
            new MemoryStream(Encoding.UTF8.GetBytes("content the reader just missed")),
            "audio/opus"
        );

        DerivedAudioStore store = StoreWith(null, () => evictor.DeleteAsync(entry.Key), null);

        Stream? stream = await store.OpenReadAsync(entry.Key);

        stream.Should().BeNull();
    }

    [Fact]
    public async Task Delete_RemovesFileAndRow()
    {
        byte[] content = Encoding.UTF8.GetBytes("delete this");
        IDerivedAudioStore store = Store();
        DerivedAudioEntry entry = await store.PutAsync(new MemoryStream(content), "audio/opus");

        await store.DeleteAsync(entry.Key);

        (await store.ExistsAsync(entry.Key)).Should().BeFalse();
        await using MediaContext read = new(_options);
        (await read.DerivedAudio.AsNoTracking().AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Evict_RemovesTheChosenFilesAndRows_AndReportsBytesFreed()
    {
        // Three puts with LastUsedAt set back by hand (3, 2, 1 days); cap = 2 files' worth.
        // Expect: the oldest is gone from disk and from the register, freed == its size.
        IDerivedAudioStore store = Store();
        DerivedAudioEntry oldest = await store.PutAsync(
            new MemoryStream(Encoding.UTF8.GetBytes("oldest content")),
            "audio/opus"
        );
        DerivedAudioEntry middle = await store.PutAsync(
            new MemoryStream(Encoding.UTF8.GetBytes("middle content")),
            "audio/opus"
        );
        DerivedAudioEntry newest = await store.PutAsync(
            new MemoryStream(Encoding.UTF8.GetBytes("newest content")),
            "audio/opus"
        );

        // A stem row pointing at the oldest key: eviction deletes the register
        // row, and the database's own cascade has to take the stem with it -
        // a stem row surviving its content is a plan that renders silence.
        // Foreign keys stay ON for this test (Microsoft.Data.Sqlite enables
        // them per connection); turning them off would prove nothing.
        Guid trackId = Guid.NewGuid();
        await using (MediaContext context = new(_options))
        {
            context.Tracks.Add(TrackRow(trackId));
            context.TrackStems.Add(
                new TrackStem
                {
                    Id = Ulid.NewUlid(),
                    TrackId = trackId,
                    Kind = "vocals",
                    Coverage = StemCoverage.Full,
                    Format = "opus",
                    SampleRate = 48000,
                    StorageKey = oldest.Key,
                    ProducerVersion = "spleeter-2stems-f16@v1",
                    CreatedAt = DateTime.UtcNow,
                }
            );
            await context.SaveChangesAsync();
        }

        DateTime now = DateTime.UtcNow;
        await using (MediaContext context = new(_options))
        {
            await context
                .DerivedAudio.Where(row => row.Key == oldest.Key)
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.LastUsedAt, now.AddDays(-3)));
            await context
                .DerivedAudio.Where(row => row.Key == middle.Key)
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.LastUsedAt, now.AddDays(-2)));
            await context
                .DerivedAudio.Where(row => row.Key == newest.Key)
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.LastUsedAt, now.AddDays(-1)));
        }

        await using (MediaContext seeded = new(_options))
        {
            (await seeded.TrackStems.AsNoTracking().CountAsync()).Should().Be(1);
        }

        long capBytes = 2 * oldest.Bytes;
        long freed = await store.EvictAsync(capBytes, TimeSpan.FromHours(1));

        freed.Should().Be(oldest.Bytes);
        (await store.ExistsAsync(oldest.Key)).Should().BeFalse();
        (await store.ExistsAsync(middle.Key)).Should().BeTrue();
        (await store.ExistsAsync(newest.Key)).Should().BeTrue();
        await using MediaContext read = new(_options);
        (await read.DerivedAudio.AsNoTracking().CountAsync()).Should().Be(2);
        (await read.TrackStems.AsNoTracking().AnyAsync(stem => stem.StorageKey == oldest.Key))
            .Should()
            .BeFalse();
    }

    /// <summary>
    /// Eviction picks its victims from a snapshot, and a plan can go stale
    /// between reading it and acting on it: a key read or touched in that
    /// window is in use again, and deleting it pulls the file out from under
    /// the run that just took it. The victim is re-checked under the same
    /// per-key lock a put takes and left alone, while the other victims the
    /// rule chose still go.
    /// </summary>
    [Fact]
    public async Task Evict_SkipsAKeyTouchedAfterTheSnapshot()
    {
        IDerivedAudioStore seeder = Store();
        DerivedAudioEntry oldest = await seeder.PutAsync(
            new MemoryStream(Encoding.UTF8.GetBytes("oldest content")),
            "audio/opus"
        );
        DerivedAudioEntry middle = await seeder.PutAsync(
            new MemoryStream(Encoding.UTF8.GetBytes("middle content")),
            "audio/opus"
        );
        DerivedAudioEntry newest = await seeder.PutAsync(
            new MemoryStream(Encoding.UTF8.GetBytes("newest content")),
            "audio/opus"
        );

        DateTime now = DateTime.UtcNow;
        await using (MediaContext context = new(_options))
        {
            await context
                .DerivedAudio.Where(row => row.Key == oldest.Key)
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.LastUsedAt, now.AddDays(-3)));
            await context
                .DerivedAudio.Where(row => row.Key == middle.Key)
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.LastUsedAt, now.AddDays(-2)));
            await context
                .DerivedAudio.Where(row => row.Key == newest.Key)
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.LastUsedAt, now.AddDays(-1)));
        }

        bool touched = false;
        DerivedAudioStore store = StoreWith(async () =>
        {
            if (touched)
            {
                return;
            }
            touched = true;
            await seeder.TouchAsync(oldest.Key);
        });

        // One file's worth of cap, so the rule chooses the two coldest keys:
        // the touched one is skipped and the other still goes.
        long freed = await store.EvictAsync(oldest.Bytes, TimeSpan.FromHours(1));

        touched.Should().BeTrue();
        freed.Should().Be(middle.Bytes);
        (await store.ExistsAsync(oldest.Key)).Should().BeTrue();
        (await store.ExistsAsync(middle.Key)).Should().BeFalse();
        (await store.ExistsAsync(newest.Key)).Should().BeTrue();
    }

    /// <summary>
    /// The victim's delete runs with the key's lock already held, so it must
    /// not take that lock again: a <see cref="SemaphoreSlim" /> is not
    /// re-entrant and the second wait would never return. Asserted against a
    /// deadline, so a regression here reports as a failed test rather than as
    /// a test run that stops.
    /// </summary>
    [Fact]
    public async Task Evict_OfASingleKey_Completes()
    {
        IDerivedAudioStore store = Store();
        DerivedAudioEntry entry = await store.PutAsync(
            new MemoryStream(Encoding.UTF8.GetBytes("the only content")),
            "audio/opus"
        );

        DateTime cold = DateTime.UtcNow.AddDays(-2);
        await using (MediaContext context = new(_options))
        {
            await context
                .DerivedAudio.Where(row => row.Key == entry.Key)
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.LastUsedAt, cold));
        }

        // The deadline is the sweep's own token: a wait on a lock it already
        // holds ends as a cancellation the assertion below reports, and
        // nothing is left running behind a test that failed.
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        Func<Task<long>> evicting = () =>
            store.EvictAsync(capBytes: 0, grace: TimeSpan.FromHours(1), ct: deadline.Token);

        long freed = (
            await evicting
                .Should()
                .NotThrowAsync("eviction must not wait on the per-key lock it already holds")
        ).Which;

        freed.Should().Be(entry.Bytes);
        (await store.ExistsAsync(entry.Key)).Should().BeFalse();
    }

    /// <summary>
    /// The mirror of the tmp/ sweep, one step further along the put: a crash
    /// between the move into place and the register insert leaves a content
    /// file no key addresses and no policy counts. Only this sweep frees it.
    /// </summary>
    [Fact]
    public async Task Evict_RemovesAContentFileThatHasNoRegisterRow()
    {
        IDerivedAudioStore store = Store();
        DerivedAudioEntry kept = await store.PutAsync(
            new MemoryStream(Encoding.UTF8.GetBytes("registered content")),
            "audio/opus"
        );

        string stalePath = PlantOrphan(new string('c', 64), DateTime.UtcNow.AddDays(-2));

        await store.EvictAsync(capBytes: long.MaxValue, grace: TimeSpan.FromHours(1));

        File.Exists(stalePath).Should().BeFalse();
        (await store.ExistsAsync(kept.Key)).Should().BeTrue();
    }

    /// <summary>
    /// The grace window is the whole reason this sweep is safe: a put that is
    /// between its own move-into-place and its register insert right now looks
    /// exactly like an orphan. A file younger than the window is left alone,
    /// so the sweep can never pull content out from under a put in flight.
    /// </summary>
    [Fact]
    public async Task Evict_LeavesAnOrphanInsideTheGraceWindow()
    {
        IDerivedAudioStore store = Store();

        string freshPath = PlantOrphan(new string('d', 64), DateTime.UtcNow);

        await store.EvictAsync(capBytes: long.MaxValue, grace: TimeSpan.FromHours(1));

        File.Exists(freshPath).Should().BeTrue();
    }

    /// <summary>
    /// Only the two-character shards a key's path is built from are content
    /// folders. Anything else under the derived root belongs to something
    /// else - tmp/ has its own sweep with its own rules - and this one must
    /// not walk into it, whatever it holds and however old.
    /// </summary>
    [Fact]
    public async Task Evict_IgnoresFoldersThatAreNotTwoCharacters()
    {
        IDerivedAudioStore store = Store();

        string outsidePath = PlantStaleFile("abc", new string('e', 64));
        string tempLikePath = PlantStaleFile("tmpx", new string('f', 64));

        await store.EvictAsync(capBytes: long.MaxValue, grace: TimeSpan.FromHours(1));

        File.Exists(outsidePath).Should().BeTrue();
        File.Exists(tempLikePath).Should().BeTrue();
    }

    /// <summary>A content file under its own shard, with no register row.</summary>
    private string PlantOrphan(string key, DateTime lastWriteUtc)
    {
        Directory.CreateDirectory(Path.Combine(_root, key[..2]));
        string path = Path.Combine(_root, key[..2], key);
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes("orphaned by a crash"));
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
        return path;
    }

    /// <summary>A file under a folder that is not a shard, old enough to be swept if it were.</summary>
    private string PlantStaleFile(string folder, string name)
    {
        Directory.CreateDirectory(Path.Combine(_root, folder));
        string path = Path.Combine(_root, folder, name);
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes("not this sweep's business"));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-2));
        return path;
    }

    [Fact]
    public async Task PutConcurrently_TheSameContentTwice_IsOneFileAndOneRow()
    {
        // The store is a process-wide singleton, so two jobs racing to produce
        // the same stem/segment share one store instance in practice — both
        // calls below go through the same store for that reason.
        byte[] content = Encoding.UTF8.GetBytes("racing content, same bytes twice");
        string expectedKey = Convert.ToHexStringLower(SHA256.HashData(content));
        IDerivedAudioStore store = Store();

        Task<DerivedAudioEntry> first = store.PutAsync(new MemoryStream(content), "audio/opus");
        Task<DerivedAudioEntry> second = store.PutAsync(new MemoryStream(content), "audio/opus");
        DerivedAudioEntry[] entries = await Task.WhenAll(first, second);

        entries[0].Key.Should().Be(expectedKey);
        entries[1].Key.Should().Be(expectedKey);
        await using MediaContext read = new(_options);
        (await read.DerivedAudio.AsNoTracking().CountAsync()).Should().Be(1);
        (await store.ExistsAsync(expectedKey)).Should().BeTrue();

        // Neither racer left an orphaned temp file behind.
        string tempDir = Path.Combine(_root, "tmp");
        if (Directory.Exists(tempDir))
        {
            Directory.GetFiles(tempDir).Should().BeEmpty();
        }
    }

    /// <summary>
    /// A key a plugin invented rather than one <see cref="IDerivedAudioStore.PutAsync" />
    /// minted: the store answers "not found" for it and never builds a path
    /// out of it, so nothing under the derived root is read, touched or
    /// removed. <see cref="IDerivedAudioStore.RelativePath" /> is the one
    /// member that throws instead, because it has no "not found" to return.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("../../etc")]
    [InlineData("ab/../../etc/passwd")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task AnInvalidKey_IsNotFound_AndNeverTouchesTheDisk(string key)
    {
        IDerivedAudioStore store = Store();
        DerivedAudioEntry planted = await store.PutAsync(
            new MemoryStream(Encoding.UTF8.GetBytes("real content")),
            "audio/opus"
        );

        (await store.ExistsAsync(key)).Should().BeFalse();
        (await store.OpenReadAsync(key)).Should().BeNull();

        Func<Task> touch = () => store.TouchAsync(key);
        await touch.Should().NotThrowAsync();
        Func<Task> delete = () => store.DeleteAsync(key);
        await delete.Should().NotThrowAsync();

        // The one real file and its row are still exactly where they were.
        (await store.ExistsAsync(planted.Key))
            .Should()
            .BeTrue();
        await using MediaContext read = new(_options);
        (await read.DerivedAudio.AsNoTracking().CountAsync()).Should().Be(1);

        Action relativePath = () => store.RelativePath(key);
        relativePath.Should().Throw<ArgumentException>();
    }

    /// <summary>
    /// Uppercase hex of the right length is still not a key this store minted:
    /// it writes lowercase, so an uppercase key resolves to a different file on
    /// a case-sensitive filesystem and the same one on Windows.
    /// </summary>
    [Fact]
    public async Task AnUppercaseKey_IsNotFound()
    {
        IDerivedAudioStore store = Store();
        DerivedAudioEntry entry = await store.PutAsync(
            new MemoryStream(Encoding.UTF8.GetBytes("cased content")),
            "audio/opus"
        );

        (await store.ExistsAsync(entry.Key.ToUpperInvariant())).Should().BeFalse();
    }

    /// <summary>
    /// The register row and the file are two halves of one entry. A file
    /// without its row is the half-written state a crash leaves behind, and
    /// answering "yes, that key is here" for it hands a caller a key that
    /// nothing will ever evict and that a foreign key will reject.
    /// </summary>
    [Fact]
    public async Task Exists_IsFalseForAFileWithoutARegisterRow()
    {
        IDerivedAudioStore store = Store();
        DerivedAudioEntry entry = await store.PutAsync(
            new MemoryStream(Encoding.UTF8.GetBytes("orphan content")),
            "audio/opus"
        );

        await using (MediaContext context = new(_options))
        {
            await context.DerivedAudio.Where(row => row.Key == entry.Key).ExecuteDeleteAsync();
        }

        File.Exists(Path.Combine(_root, entry.Key[..2], entry.Key)).Should().BeTrue();
        (await store.ExistsAsync(entry.Key)).Should().BeFalse();
    }

    [Fact]
    public async Task Evict_SweepsStaleTempFiles()
    {
        IDerivedAudioStore store = Store();
        // A crash between the temp write and the move-into-place is the only
        // thing that should ever leave a file under tmp/ — simulate it by
        // planting one directly, since PutAsync always cleans up after itself.
        string tempDir = Path.Combine(_root, "tmp");
        Directory.CreateDirectory(tempDir);
        string stalePath = Path.Combine(tempDir, "stale-orphan");
        string freshPath = Path.Combine(tempDir, "fresh-orphan");
        await File.WriteAllBytesAsync(stalePath, Encoding.UTF8.GetBytes("orphaned by a crash"));
        await File.WriteAllBytesAsync(freshPath, Encoding.UTF8.GetBytes("still being written"));
        File.SetLastWriteTimeUtc(stalePath, DateTime.UtcNow.AddDays(-2));
        File.SetLastWriteTimeUtc(freshPath, DateTime.UtcNow);

        await store.EvictAsync(capBytes: long.MaxValue, grace: TimeSpan.FromHours(1));

        File.Exists(stalePath).Should().BeFalse();
        File.Exists(freshPath).Should().BeTrue();
    }
}
