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

using NoMercy.Storage.Remote;
using NoMercy.Storage.Validation;
using NoMercy.Tests.Storage.Fakes;

namespace NoMercy.Tests.Storage;

/// <summary>
/// Behavioral contract tests for <see cref="RemoteStorage"/> — the
/// <see cref="IStorage"/> facade every remote driver (S3/R2, NFS, SMB, WebDAV)
/// is wrapped in. Unlike <see cref="RemoteStoragePathGuardTests"/> (which only
/// proves absolute paths are rejected), this file demands the actual behavior
/// of every facade method: what gets returned, what gets written, and that
/// sync/async companions agree. A real in-memory driver
/// (<see cref="InMemoryStorageDriver"/>) stands in for the network backend so
/// these tests exercise real stream copies and real byte content rather than
/// asserting against a mock's call log.
/// </summary>
[Trait("Category", "Unit")]
public sealed class RemoteStorageTests
{
    private static RemoteStorage NewStorage(out InMemoryStorageDriver driver)
    {
        driver = new();
        return new(driver);
    }

    // ------------------------------------------------------------------
    // Constructor guard
    // ------------------------------------------------------------------

    [Fact]
    public void Constructor_rejects_null_driver()
    {
        Action act = () => new RemoteStorage(null!);

        act.Should()
            .Throw<ArgumentNullException>("RemoteStorage must not silently accept a null driver");
    }

    [Fact]
    public void Driver_property_exposes_underlying_driver()
    {
        RemoteStorage storage = NewStorage(out InMemoryStorageDriver driver);

        storage
            .Driver.Should()
            .BeSameAs(driver, "Driver must expose the exact instance passed to the constructor");
    }

    // ------------------------------------------------------------------
    // Read / Write round trip (async)
    // ------------------------------------------------------------------

    [Fact]
    public async Task WriteAsync_then_ReadAsync_round_trips_bytes()
    {
        RemoteStorage storage = NewStorage(out _);
        byte[] payload = [0x01, 0x02, 0x03, 0xFF];

        await storage.WriteAsync("movies/avatar.mkv", payload, CancellationToken.None);
        byte[] result = await storage.ReadAsync("movies/avatar.mkv", CancellationToken.None);

        result.Should().Equal(payload, "bytes written must be exactly the bytes read back");
    }

    [Fact]
    public async Task OpenReadAsync_returns_stream_positioned_at_start()
    {
        RemoteStorage storage = NewStorage(out InMemoryStorageDriver driver);
        driver.SeedFile("clip.mp4", [0xAA, 0xBB, 0xCC]);

        await using Stream stream = await storage.OpenReadAsync("clip.mp4", CancellationToken.None);
        byte[] buffer = new byte[3];
        int read = await stream.ReadAsync(buffer, CancellationToken.None);

        read.Should().Be(3);
        buffer.Should().Equal(0xAA, 0xBB, 0xCC);
    }

    [Fact]
    public async Task OpenWriteAsync_overwrite_false_throws_when_object_exists()
    {
        RemoteStorage storage = NewStorage(out InMemoryStorageDriver driver);
        driver.SeedFile("existing.txt", [0x01]);

        Func<Task> act = async () =>
        {
            await using Stream s = await storage.OpenWriteAsync(
                "existing.txt",
                overwrite: false,
                CancellationToken.None
            );
        };

        await act.Should()
            .ThrowAsync<IOException>(
                "overwrite:false must not silently clobber an existing object"
            );
    }

    // ------------------------------------------------------------------
    // ExistsAsync
    // ------------------------------------------------------------------

    [Fact]
    public async Task ExistsAsync_true_for_file_and_directory_false_otherwise()
    {
        RemoteStorage storage = NewStorage(out InMemoryStorageDriver driver);
        driver.SeedFile("a/file.txt", [0x01]);
        driver.SeedDirectory("a/dir");

        (await storage.ExistsAsync("a/file.txt", CancellationToken.None)).Should().BeTrue();
        (await storage.ExistsAsync("a/dir", CancellationToken.None)).Should().BeTrue();
        (await storage.ExistsAsync("a/missing", CancellationToken.None)).Should().BeFalse();
    }

    // ------------------------------------------------------------------
    // DeleteAsync / DeleteDirectoryAsync — idempotent on missing
    // ------------------------------------------------------------------

    [Fact]
    public async Task DeleteAsync_removes_existing_file_and_is_noop_when_missing()
    {
        RemoteStorage storage = NewStorage(out InMemoryStorageDriver driver);
        driver.SeedFile("gone.txt", [0x01]);

        await storage.DeleteAsync("gone.txt", CancellationToken.None);
        driver.FileExists("gone.txt").Should().BeFalse();

        Func<Task> act = async () => await storage.DeleteAsync("gone.txt", CancellationToken.None);
        await act.Should()
            .NotThrowAsync("deleting an already-missing file must be a no-op, not an error");
    }

    [Fact]
    public async Task DeleteDirectoryAsync_removes_existing_and_is_noop_when_missing()
    {
        RemoteStorage storage = NewStorage(out InMemoryStorageDriver driver);
        driver.SeedDirectory("old-lib");

        await storage.DeleteDirectoryAsync("old-lib", recursive: true, CancellationToken.None);
        driver.DirectoryExists("old-lib").Should().BeFalse();

        Func<Task> act = async () =>
            await storage.DeleteDirectoryAsync("old-lib", recursive: true, CancellationToken.None);
        await act.Should().NotThrowAsync("deleting an already-missing directory must be a no-op");
    }

    // ------------------------------------------------------------------
    // CreateDirectoryAsync
    // ------------------------------------------------------------------

    [Fact]
    public async Task CreateDirectoryAsync_delegates_to_driver()
    {
        RemoteStorage storage = NewStorage(out InMemoryStorageDriver driver);

        await storage.CreateDirectoryAsync("new/nested/dir", CancellationToken.None);

        driver.DirectoryExists("new/nested/dir").Should().BeTrue();
    }

    // ------------------------------------------------------------------
    // MoveAsync / CopyAsync
    // ------------------------------------------------------------------

    [Fact]
    public async Task MoveAsync_moves_bytes_and_removes_source()
    {
        RemoteStorage storage = NewStorage(out InMemoryStorageDriver driver);
        driver.SeedFile("src.txt", [0x42]);

        await storage.MoveAsync("src.txt", "dst.txt", CancellationToken.None);

        driver.FileExists("src.txt").Should().BeFalse("source must be removed after move");
        (await storage.ReadAsync("dst.txt", CancellationToken.None)).Should().Equal(0x42);
    }

    [Fact]
    public async Task CopyAsync_duplicates_bytes_and_keeps_source()
    {
        RemoteStorage storage = NewStorage(out InMemoryStorageDriver driver);
        driver.SeedFile("src.txt", [0x7A]);

        await storage.CopyAsync("src.txt", "dst.txt", CancellationToken.None);

        driver.FileExists("src.txt").Should().BeTrue("copy must keep the source");
        (await storage.ReadAsync("dst.txt", CancellationToken.None)).Should().Equal(0x7A);
    }

    // ------------------------------------------------------------------
    // SizeAsync / LastModifiedAsync
    // ------------------------------------------------------------------

    [Fact]
    public async Task SizeAsync_returns_byte_length()
    {
        RemoteStorage storage = NewStorage(out InMemoryStorageDriver driver);
        driver.SeedFile("file.bin", new byte[128]);

        long size = await storage.SizeAsync("file.bin", CancellationToken.None);

        size.Should().Be(128);
    }

    [Fact]
    public async Task LastModifiedAsync_returns_utc_offset_with_zero_offset()
    {
        RemoteStorage storage = NewStorage(out InMemoryStorageDriver driver);
        driver.SeedFile("file.bin", [0x01]);

        DateTimeOffset result = await storage.LastModifiedAsync("file.bin", CancellationToken.None);

        result.Offset.Should().Be(TimeSpan.Zero, "remote drivers always report UTC");
    }

    // ------------------------------------------------------------------
    // ListAsync
    // ------------------------------------------------------------------

    [Fact]
    public async Task ListAsync_yields_scope_relative_entries()
    {
        RemoteStorage storage = NewStorage(out InMemoryStorageDriver driver);
        driver.SeedFile("season1/e01.mkv", new byte[10]);
        driver.SeedFile("season1/e02.mkv", new byte[20]);

        List<StorageEntry> entries = [];
        await foreach (
            StorageEntry entry in storage.ListAsync(
                "season1",
                null,
                recursive: false,
                CancellationToken.None
            )
        )
            entries.Add(entry);

        entries.Should().HaveCount(2);
        entries.Select(e => e.Path).Should().Contain(["season1/e01.mkv", "season1/e02.mkv"]);
    }

    [Fact]
    public async Task ListAsync_honors_cancellation()
    {
        RemoteStorage storage = NewStorage(out InMemoryStorageDriver driver);
        driver.SeedFile("a.txt", [0x01]);
        using CancellationTokenSource cts = new();
        cts.Cancel();

        Func<Task> act = async () =>
        {
            await foreach (
                StorageEntry _ in storage.ListAsync("", null, recursive: true, cts.Token)
            ) { }
        };

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ------------------------------------------------------------------
    // HashAsync
    // ------------------------------------------------------------------

    [Fact]
    public async Task HashAsync_sha256_matches_known_digest()
    {
        RemoteStorage storage = NewStorage(out InMemoryStorageDriver driver);
        driver.SeedFile("hello.txt", [.. "hello"u8]);

        string hash = await storage.HashAsync("hello.txt", "SHA256", CancellationToken.None);

        hash.Should()
            .Be(
                "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824",
                "sha256('hello') is a well-known digest; a wrong result means the algorithm dispatch broke"
            );
    }

    // ------------------------------------------------------------------
    // AcquireLocalPathAsync (async) — stages to temp file, cleans up on dispose
    // ------------------------------------------------------------------

    [Fact]
    public async Task AcquireLocalPathAsync_stages_remote_object_to_temp_and_cleans_up()
    {
        RemoteStorage storage = NewStorage(out InMemoryStorageDriver driver);
        byte[] payload = [1, 2, 3, 4, 5];
        driver.SeedFile("remote/video.mp4", payload);

        LocalPathLease lease = await storage.AcquireLocalPathAsync(
            "remote/video.mp4",
            CancellationToken.None
        );
        try
        {
            File.Exists(lease.Path).Should().BeTrue("lease must materialize a real local file");
            File.ReadAllBytes(lease.Path).Should().Equal(payload);
        }
        finally
        {
            await lease.DisposeAsync();
        }

        File.Exists(lease.Path).Should().BeFalse("dispose must delete the staged temp file");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcquireLocalPathAsync_removes_partial_file_when_copy_fails(bool cancelled)
    {
        Exception failure = cancelled
            ? new OperationCanceledException("remote read cancelled")
            : new IOException("remote read failed");
        Mock<IStorageDriver> driver = new();
        driver
            .Setup(d => d.OpenReadIsolated("remote.bin"))
            .Returns(() => new FailingReadStream(failure));
        RemoteStorage storage = new(driver.Object);
        HashSet<string> before = RemoteTempFiles();

        try
        {
            Func<Task> act = () =>
                storage.AcquireLocalPathAsync("remote.bin", CancellationToken.None);
            if (cancelled)
                await act.Should().ThrowAsync<OperationCanceledException>();
            else
                await act.Should().ThrowAsync<IOException>();

            RemoteTempFiles().Should().BeEquivalentTo(before);
        }
        finally
        {
            DeleteNewRemoteTempFiles(before);
        }
    }

    // ------------------------------------------------------------------
    // Sync companions
    // ------------------------------------------------------------------

    [Fact]
    public void Exists_true_for_file_and_directory_false_otherwise()
    {
        RemoteStorage storage = NewStorage(out InMemoryStorageDriver driver);
        driver.SeedFile("f.txt", [0x01]);
        driver.SeedDirectory("d");

        storage.Exists("f.txt").Should().BeTrue();
        storage.Exists("d").Should().BeTrue();
        storage.Exists("missing").Should().BeFalse();
    }

    [Fact]
    public void SizeOrZero_returns_size_for_existing_and_zero_for_missing()
    {
        RemoteStorage storage = NewStorage(out InMemoryStorageDriver driver);
        driver.SeedFile("f.txt", new byte[42]);

        storage.SizeOrZero("f.txt").Should().Be(42);
        storage.SizeOrZero("missing.txt").Should().Be(0);
    }

    [Fact]
    public void Size_returns_driver_reported_size()
    {
        RemoteStorage storage = NewStorage(out InMemoryStorageDriver driver);
        driver.SeedFile("f.txt", new byte[7]);

        storage.Size("f.txt").Should().Be(7);
    }

    [Fact]
    public void LastModified_returns_utc_offset()
    {
        RemoteStorage storage = NewStorage(out InMemoryStorageDriver driver);
        driver.SeedFile("f.txt", [0x01]);

        storage.LastModified("f.txt").Offset.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void CreateDirectory_delegates_to_driver()
    {
        RemoteStorage storage = NewStorage(out InMemoryStorageDriver driver);

        storage.CreateDirectory("brand/new");

        driver.DirectoryExists("brand/new").Should().BeTrue();
    }

    [Fact]
    public void Delete_removes_existing_and_is_noop_when_missing()
    {
        RemoteStorage storage = NewStorage(out InMemoryStorageDriver driver);
        driver.SeedFile("f.txt", [0x01]);

        storage.Delete("f.txt");
        driver.FileExists("f.txt").Should().BeFalse();

        Action act = () => storage.Delete("f.txt");
        act.Should().NotThrow("deleting an already-missing file is idempotent");
    }

    [Fact]
    public void DeleteDirectory_removes_existing_and_is_noop_when_missing()
    {
        RemoteStorage storage = NewStorage(out InMemoryStorageDriver driver);
        driver.SeedDirectory("d");

        storage.DeleteDirectory("d", recursive: false);
        driver.DirectoryExists("d").Should().BeFalse();

        Action act = () => storage.DeleteDirectory("d", recursive: false);
        act.Should().NotThrow("deleting an already-missing directory is idempotent");
    }

    [Fact]
    public void Read_returns_full_content_synchronously()
    {
        RemoteStorage storage = NewStorage(out InMemoryStorageDriver driver);
        driver.SeedFile("f.txt", [0x9, 0x8, 0x7]);

        storage.Read("f.txt").Should().Equal(0x9, 0x8, 0x7);
    }

    [Fact]
    public void OpenRead_returns_a_readable_stream()
    {
        RemoteStorage storage = NewStorage(out InMemoryStorageDriver driver);
        driver.SeedFile("f.txt", [0x1]);

        using Stream stream = storage.OpenRead("f.txt");

        stream.CanRead.Should().BeTrue();
    }

    [Fact]
    public void OpenWrite_then_dispose_commits_bytes()
    {
        RemoteStorage storage = NewStorage(out InMemoryStorageDriver driver);

        using (Stream stream = storage.OpenWrite("out.txt", overwrite: true))
            stream.Write([0x5, 0x6], 0, 2);

        driver.FileExists("out.txt").Should().BeTrue();
    }

    [Fact]
    public void Write_writes_bytes_synchronously()
    {
        RemoteStorage storage = NewStorage(out InMemoryStorageDriver driver);

        storage.Write("out.txt", [0x1, 0x2, 0x3]);

        driver.FileExists("out.txt").Should().BeTrue();
        storage.Read("out.txt").Should().Equal(0x1, 0x2, 0x3);
    }

    [Fact]
    public void Move_moves_bytes_synchronously()
    {
        RemoteStorage storage = NewStorage(out InMemoryStorageDriver driver);
        driver.SeedFile("a.txt", [0x9]);

        storage.Move("a.txt", "b.txt");

        driver.FileExists("a.txt").Should().BeFalse();
        storage.Read("b.txt").Should().Equal(0x9);
    }

    [Fact]
    public void Copy_duplicates_bytes_synchronously()
    {
        RemoteStorage storage = NewStorage(out InMemoryStorageDriver driver);
        driver.SeedFile("a.txt", [0x9]);

        storage.Copy("a.txt", "b.txt");

        driver.FileExists("a.txt").Should().BeTrue();
        storage.Read("b.txt").Should().Equal(0x9);
    }

    [Fact]
    public void List_returns_entries_with_size_and_modified_time()
    {
        RemoteStorage storage = NewStorage(out InMemoryStorageDriver driver);
        driver.SeedFile("dir/a.txt", new byte[3]);

        IReadOnlyList<StorageEntry> entries = storage.List("dir", null, recursive: false);

        entries.Should().ContainSingle();
        entries[0].SizeBytes.Should().Be(3);
    }

    [Fact]
    public void AcquireLocalPath_sync_stages_and_cleans_up()
    {
        RemoteStorage storage = NewStorage(out InMemoryStorageDriver driver);
        byte[] payload = [7, 7, 7];
        driver.SeedFile("remote.bin", payload);

        LocalPathLease lease = storage.AcquireLocalPath("remote.bin");
        try
        {
            File.ReadAllBytes(lease.Path).Should().Equal(payload);
        }
        finally
        {
            lease.Dispose();
        }

        File.Exists(lease.Path)
            .Should()
            .BeFalse("sync AcquireLocalPath must also clean up on dispose");
    }

    [Fact]
    public void AcquireLocalPath_removes_partial_file_when_copy_fails()
    {
        Mock<IStorageDriver> driver = new();
        driver
            .Setup(d => d.OpenReadIsolated("remote.bin"))
            .Returns(() => new FailingReadStream(new IOException("remote read failed")));
        RemoteStorage storage = new(driver.Object);
        HashSet<string> before = RemoteTempFiles();

        try
        {
            Action act = () => storage.AcquireLocalPath("remote.bin");
            act.Should().Throw<IOException>();
            RemoteTempFiles().Should().BeEquivalentTo(before);
        }
        finally
        {
            DeleteNewRemoteTempFiles(before);
        }
    }

    private static HashSet<string> RemoteTempFiles() =>
        Directory.Exists(StoragePaths.TempRoot)
            ? Directory.GetFiles(StoragePaths.TempRoot, "nomercy-remote-*").ToHashSet()
            : [];

    private static void DeleteNewRemoteTempFiles(HashSet<string> before)
    {
        foreach (string path in RemoteTempFiles().Except(before))
            File.Delete(path);
    }

    private sealed class FailingReadStream(Exception failure) : Stream
    {
        private bool _readOnce;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_readOnce)
                throw failure;
            _readOnce = true;
            buffer[offset] = 42;
            return 1;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        )
        {
            if (_readOnce)
                return ValueTask.FromException<int>(failure);
            _readOnce = true;
            buffer.Span[0] = 42;
            return ValueTask.FromResult(1);
        }

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    [Fact]
    public async Task ReadAllTextAsync_decodes_utf8_content()
    {
        RemoteStorage storage = NewStorage(out InMemoryStorageDriver driver);
        driver.SeedFile("readme.txt", System.Text.Encoding.UTF8.GetBytes("hello world"));

        string text = await storage.ReadAllTextAsync("readme.txt", CancellationToken.None);

        text.Should().Be("hello world");
    }

    [Fact]
    public async Task WriteAllTextAsync_writes_utf8_bytes()
    {
        RemoteStorage storage = NewStorage(out InMemoryStorageDriver driver);

        await storage.WriteAllTextAsync("out.txt", "some content", CancellationToken.None);

        driver.FileExists("out.txt").Should().BeTrue();
        string readBack = await storage.ReadAllTextAsync("out.txt", CancellationToken.None);
        readBack.Should().Be("some content");
    }

    [Fact]
    public async Task MoveDirectoryAsync_delegates_to_driver()
    {
        RemoteStorage storage = NewStorage(out InMemoryStorageDriver driver);
        driver.SeedDirectory("old");

        await storage.MoveDirectoryAsync("old", "new", CancellationToken.None);

        driver.MoveDirectoryCallCount.Should().Be(1);
    }

    [Fact]
    public void MoveDirectory_sync_delegates_to_driver()
    {
        RemoteStorage storage = NewStorage(out InMemoryStorageDriver driver);
        driver.SeedDirectory("old");

        storage.MoveDirectory("old", "new");

        driver.MoveDirectoryCallCount.Should().Be(1);
    }

    // ------------------------------------------------------------------
    // Rule 6 — GetFullPath escape hatch is LocalStorage-only; RemoteStorage
    // does not override it, so the IStorage default must throw.
    // ------------------------------------------------------------------

    [Fact]
    public void GetFullPath_throws_NotSupportedException_for_remote_storage()
    {
        RemoteStorage storage = NewStorage(out _);
        IStorage asInterface = storage;

        Action act = () => asInterface.GetFullPath("anything.txt");

        act.Should()
            .Throw<NotSupportedException>(
                "GetFullPath escapes the abstraction and is only valid for LocalStorage; "
                    + "remote drivers have no meaningful local filesystem path"
            );
    }

    // ------------------------------------------------------------------
    // CombinePath / DirectorySeparator default-interface members, exercised
    // through the facade exactly like production S3/SMB/WebDAV drivers do
    // (none of those override CombinePath or DirectorySeparator either).
    // ------------------------------------------------------------------

    [Fact]
    public void DirectorySeparator_defaults_to_forward_slash_for_remote_drivers()
    {
        IStorage storage = NewStorage(out _);

        storage
            .DirectorySeparator.Should()
            .Be('/', "remote drivers speak '/' regardless of host OS");
    }

    [Fact]
    public void CombinePath_default_implementation_joins_with_forward_slash()
    {
        IStorage storage = NewStorage(out _);

        storage.CombinePath("season1", "e01.mkv").Should().Be("season1/e01.mkv");
    }

    [Fact]
    public void CombinePath_default_implementation_trims_redundant_separators()
    {
        IStorage storage = NewStorage(out _);

        storage.CombinePath("season1/", "/e01.mkv").Should().Be("season1/e01.mkv");
    }

    [Fact]
    public void CombinePath_default_implementation_handles_empty_segments()
    {
        IStorage storage = NewStorage(out _);

        storage.CombinePath("", "child").Should().Be("child");
        storage.CombinePath("parent", "").Should().Be("parent");
    }

    // ------------------------------------------------------------------
    // BackendLabel default fallback (GetType().Name) — no production driver
    // overrides it away from a real label, but the fallback is a documented
    // contract for future/test drivers and must resolve to the type name.
    // ------------------------------------------------------------------

    [Fact]
    public void BackendLabel_defaults_to_the_concrete_type_name()
    {
        RemoteStorage storage = NewStorage(out InMemoryStorageDriver driver);

        storage.Driver.BackendLabel.Should().Be(nameof(InMemoryStorageDriver));
    }

    // ------------------------------------------------------------------
    // TryGetPresignedUrlAsync default fallback — null for drivers that don't
    // support presigning (local / NFS / WebDAV in production).
    // ------------------------------------------------------------------

    [Fact]
    public async Task TryGetPresignedUrlAsync_defaults_to_null_when_driver_does_not_support_it()
    {
        RemoteStorage storage = NewStorage(out _);
        IStorage asInterface = storage;

        Uri? result = await asInterface.TryGetPresignedUrlAsync(
            "f.txt",
            TimeSpan.FromMinutes(5),
            CancellationToken.None
        );

        result.Should().BeNull();
    }

    // ------------------------------------------------------------------
    // IStorageDriver.AcquireLocalPathAsync default-interface-member — used
    // directly (not via RemoteStorage, which has its own override) by
    // FfProbeService for every driver EXCEPT LocalStorageDriver (S3/NFS/SMB/
    // WebDAV all fall through to this default). Must stage to a real temp
    // file under StoragePaths.TempRoot and clean it up on lease dispose.
    // ------------------------------------------------------------------

    [Fact]
    public async Task IStorageDriver_default_AcquireLocalPathAsync_stages_and_cleans_up()
    {
        InMemoryStorageDriver driver = new();
        byte[] payload = [9, 8, 7, 6];
        driver.SeedFile("clip.mkv", payload);
        IStorageDriver asInterface = driver;

        LocalPathLease lease = await asInterface.AcquireLocalPathAsync(
            "clip.mkv",
            CancellationToken.None
        );
        try
        {
            Path.IsPathRooted(lease.Path)
                .Should()
                .BeTrue("the default implementation must materialize a real OS path");
            File.ReadAllBytes(lease.Path).Should().Equal(payload);
        }
        finally
        {
            await lease.DisposeAsync();
        }

        File.Exists(lease.Path)
            .Should()
            .BeFalse("the default implementation's cleanup callback must delete the staged file");
    }

    // ------------------------------------------------------------------
    // Absolute-path rejection happens BEFORE the driver is ever touched —
    // regression guard for the V() gate on every entry point not already
    // covered by RemoteStoragePathGuardTests.
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("/abs/path")]
    [InlineData(@"C:\abs\path")]
    public void Move_rejects_absolute_source_or_destination(string absolute)
    {
        RemoteStorage storage = NewStorage(out InMemoryStorageDriver driver);

        Action act = () => storage.Move(absolute, "dest.txt");

        act.Should().Throw<StoragePathNotAllowedException>();
        driver
            .FileExists("dest.txt")
            .Should()
            .BeFalse("driver must never be reached for a rejected path");
    }
}
