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
/// Marking a file sparse on NTFS, so a download written out of order does not
/// make the file system reserve and zero-fill the whole length first.
/// </summary>
[Trait("Category", "Unit")]
public sealed class SparseFileTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"nm-sparse-{Guid.NewGuid():N}"
    );

    public SparseFileTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException) { }
    }

    /// <summary>
    /// The moment it is needed is right after the writer created the file,
    /// before it sets the length, while it still holds the file open for
    /// writing. The mark must work then.
    /// </summary>
    [SkippableFact]
    public void A_file_its_writer_still_holds_open_is_marked_sparse()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Only NTFS needs the flag set.");
        string path = Path.Combine(_root, "download.bin");

        using FileStream writer = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);

        SparseFile.TryMark(path).Should().BeTrue();
        File.GetAttributes(path).Should().HaveFlag(FileAttributes.SparseFile);
    }

    [SkippableFact]
    public void A_file_that_does_not_exist_is_not_marked()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Only NTFS needs the flag set.");

        SparseFile.TryMark(Path.Combine(_root, "missing.bin")).Should().BeFalse();
    }

    [SkippableFact]
    public void Off_windows_nothing_is_done()
    {
        Skip.If(OperatingSystem.IsWindows(), "Windows is the one platform that marks.");
        string path = Path.Combine(_root, "download.bin");
        File.WriteAllBytes(path, []);

        SparseFile.TryMark(path).Should().BeFalse();
    }

    [SkippableFact]
    public void Local_storage_marks_a_file_inside_its_root()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Only NTFS needs the flag set.");
        IStorage storage = Storage();
        string path = Path.Combine(_root, "inside.bin");
        File.WriteAllBytes(path, []);

        storage.TryMarkSparse(path).Should().BeTrue();
        File.GetAttributes(path).Should().HaveFlag(FileAttributes.SparseFile);
    }

    /// <summary>The same guard as every other path: outside the root is refused, not marked.</summary>
    [Fact]
    public void Local_storage_refuses_a_file_outside_its_root()
    {
        IStorage storage = Storage();
        string outside = Path.Combine(Path.GetTempPath(), $"nm-outside-{Guid.NewGuid():N}.bin");

        Action mark = () => storage.TryMarkSparse(outside);

        mark.Should().Throw<Exception>();
    }

    /// <summary>A backend that is not a local disk has nothing to mark.</summary>
    [Fact]
    public void A_backend_that_is_not_local_answers_false()
    {
        Mock<IStorage> remote = new() { CallBase = true };

        remote.Object.TryMarkSparse("anything.bin").Should().BeFalse();
    }

    private IStorage Storage()
    {
        LocalStorageDriver driver = new();

        return new LocalStorage(driver, new StoragePathGuard([_root], driver));
    }
}
