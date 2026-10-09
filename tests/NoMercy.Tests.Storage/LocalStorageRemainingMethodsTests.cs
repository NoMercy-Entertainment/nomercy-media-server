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

using NoMercy.Storage.Drivers.Local;
using NoMercy.Storage.Validation;

namespace NoMercy.Tests.Storage;

/// <summary>
/// Fills the gaps <see cref="IStorageFacadeTests"/> leaves in
/// <see cref="LocalStorage"/>: constructor null-guards, the sync/async
/// method pairs it doesn't exercise (OpenReadAsync, Size, LastModified,
/// OpenRead, OpenWrite, MoveDirectory/Async), and the
/// "listed entry equals the scope root exactly" branch of
/// <c>ToScopeRelative</c>. Uses a REAL <see cref="LocalStorageDriver"/>
/// against a REAL temp directory — this facade's whole job is talking to
/// the OS filesystem, so a mock of that filesystem would not prove it works.
/// </summary>
[Trait("Category", "Unit")]
public sealed class LocalStorageRemainingMethodsTests : IDisposable
{
    private readonly string _root;
    private readonly LocalStorageDriver _driver = new();
    private readonly StoragePathGuard _guard;
    private readonly LocalStorage _storage;

    public LocalStorageRemainingMethodsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"nm-lsrm-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _guard = new([_root], _driver);
        _storage = new(_driver, _guard);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    [Fact]
    public void Constructor_rejects_null_driver()
    {
        Action act = () => new LocalStorage(null!, _guard);

        act.Should().Throw<ArgumentNullException>().WithParameterName("driver");
    }

    [Fact]
    public void Constructor_rejects_null_guard()
    {
        Action act = () => new LocalStorage(_driver, null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("guard");
    }

    [Fact]
    public async Task OpenReadAsync_returns_a_stream_over_the_real_file()
    {
        File.WriteAllBytes(Path.Combine(_root, "clip.bin"), [1, 2, 3]);

        await using Stream stream = await _storage.OpenReadAsync(
            "clip.bin",
            CancellationToken.None
        );
        byte[] buffer = new byte[3];
        await stream.ReadExactlyAsync(buffer);

        buffer.Should().Equal(1, 2, 3);
    }

    [Fact]
    public void Size_returns_real_file_length()
    {
        File.WriteAllBytes(Path.Combine(_root, "sized.bin"), new byte[777]);

        _storage.Size("sized.bin").Should().Be(777);
    }

    [Fact]
    public void LastModified_reflects_a_recent_real_write()
    {
        File.WriteAllText(Path.Combine(_root, "recent.txt"), "x");

        _storage
            .LastModified("recent.txt")
            .UtcDateTime.Should()
            .BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void OpenRead_sync_returns_a_readable_stream_over_the_real_file()
    {
        File.WriteAllBytes(Path.Combine(_root, "readme.bin"), [9, 8, 7]);

        using Stream stream = _storage.OpenRead("readme.bin");
        byte[] buffer = new byte[3];
        stream.ReadExactly(buffer);

        buffer.Should().Equal(9, 8, 7);
    }

    [Fact]
    public void OpenWrite_sync_creates_parent_directories_and_writes_real_bytes()
    {
        using (Stream stream = _storage.OpenWrite("deep/nested/out.bin", overwrite: true))
            stream.Write([4, 5, 6], 0, 3);

        File.ReadAllBytes(Path.Combine(_root, "deep", "nested", "out.bin")).Should().Equal(4, 5, 6);
    }

    [Fact]
    public async Task MoveDirectoryAsync_moves_a_real_directory_tree()
    {
        Directory.CreateDirectory(Path.Combine(_root, "olddir"));
        File.WriteAllText(Path.Combine(_root, "olddir", "f.txt"), "x");

        await _storage.MoveDirectoryAsync("olddir", "newdir", CancellationToken.None);

        Directory.Exists(Path.Combine(_root, "olddir")).Should().BeFalse();
        File.Exists(Path.Combine(_root, "newdir", "f.txt")).Should().BeTrue();
    }

    [Fact]
    public async Task MoveDirectoryAsync_creates_missing_destination_parent()
    {
        Directory.CreateDirectory(Path.Combine(_root, "async-source"));
        File.WriteAllText(Path.Combine(_root, "async-source", "f.txt"), "x");

        await _storage.MoveDirectoryAsync(
            "async-source",
            "new/parent/async-destination",
            CancellationToken.None
        );

        Directory.Exists(Path.Combine(_root, "async-source")).Should().BeFalse();
        File.Exists(Path.Combine(_root, "new", "parent", "async-destination", "f.txt"))
            .Should()
            .BeTrue();
    }

    [Fact]
    public void MoveDirectory_sync_moves_a_real_directory_tree()
    {
        Directory.CreateDirectory(Path.Combine(_root, "olddir2"));
        File.WriteAllText(Path.Combine(_root, "olddir2", "f.txt"), "x");

        _storage.MoveDirectory("olddir2", "newdir2");

        Directory.Exists(Path.Combine(_root, "olddir2")).Should().BeFalse();
        File.Exists(Path.Combine(_root, "newdir2", "f.txt")).Should().BeTrue();
    }

    [Fact]
    public void MoveDirectory_sync_creates_missing_destination_parent()
    {
        Directory.CreateDirectory(Path.Combine(_root, "sync-source"));
        File.WriteAllText(Path.Combine(_root, "sync-source", "f.txt"), "x");

        _storage.MoveDirectory("sync-source", "new/parent/sync-destination");

        Directory.Exists(Path.Combine(_root, "sync-source")).Should().BeFalse();
        File.Exists(Path.Combine(_root, "new", "parent", "sync-destination", "f.txt"))
            .Should()
            .BeTrue();
    }

    [Fact]
    public void List_entry_whose_absolute_path_equals_the_scope_root_exactly_maps_to_empty_string()
    {
        // Regression for ToScopeRelative's "listed entry equals root exactly"
        // branch: a driver that yields the scope root itself as one of its
        // own entries (some backends self-list the directory node) must map
        // to the empty string — the canonical "this is the root" scope-
        // relative path — not a leaked absolute path or a stray "/".
        Mock<IStorageDriver> driver = new(MockBehavior.Loose);
        driver.Setup(d => d.GetFullPath(It.IsAny<string>())).Returns<string>(Path.GetFullPath);
        driver.Setup(d => d.ResolveLinkTarget(It.IsAny<string>())).Returns((string?)null);
        driver
            .Setup(d =>
                d.EnumerateEntries(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<SearchOption>())
            )
            .Returns([new StorageEntryInfo(_root, true, 0L, DateTime.UtcNow)]);
        StoragePathGuard guard = new([_root], driver.Object);
        LocalStorage storage = new(driver.Object, guard);

        IReadOnlyList<StorageEntry> entries = storage.List("", null, recursive: false);

        entries.Should().ContainSingle();
        entries[0]
            .Path.Should()
            .Be(string.Empty, "the root entry itself must map to the empty scope-relative path");
    }
}
