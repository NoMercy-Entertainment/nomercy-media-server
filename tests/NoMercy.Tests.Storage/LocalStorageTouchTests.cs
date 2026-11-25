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
using Moq;
using NoMercy.Storage;
using NoMercy.Storage.Drivers.Local;
using NoMercy.Storage.Validation;
using Xunit;

namespace NoMercy.Tests.Storage;

[Trait("Category", "Unit")]
public sealed class LocalStorageTouchTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"nm-touch-{Guid.NewGuid():N}"
    );

    public LocalStorageTouchTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private IStorage Storage()
    {
        LocalStorageDriver driver = new();
        return new LocalStorage(driver, new StoragePathGuard([_root], driver));
    }

    [Fact]
    public void TryTouch_MovesTheLastWriteTimeToNow()
    {
        string path = Path.Combine(_root, "a.jpg");
        File.WriteAllBytes(path, [1, 2, 3]);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-30));

        bool touched = Storage().TryTouch(path);

        touched.Should().BeTrue();
        File.GetLastWriteTimeUtc(path).Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
        File.ReadAllBytes(path).Should().Equal(1, 2, 3);
    }

    [Fact]
    public void TryTouch_ForAFileThatDoesNotExist_ReturnsFalseAndCreatesNothing()
    {
        string path = Path.Combine(_root, "missing.jpg");

        bool touched = Storage().TryTouch(path);

        touched.Should().BeFalse();
        File.Exists(path).Should().BeFalse();
    }

    [Fact]
    public void TryTouch_ForAFileOutsideTheScope_IsRefused()
    {
        string outside = Path.Combine(Path.GetTempPath(), $"nm-outside-{Guid.NewGuid():N}.jpg");
        File.WriteAllBytes(outside, [1]);
        DateTime old = DateTime.UtcNow.AddDays(-30);
        File.SetLastWriteTimeUtc(outside, old);
        try
        {
            Action touch = () => Storage().TryTouch(outside);

            touch.Should().Throw<Exception>();
            File.GetLastWriteTimeUtc(outside).Should().BeCloseTo(old, TimeSpan.FromSeconds(5));
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public void TryTouch_OnABackendThatIsNotLocal_ReturnsFalse()
    {
        Mock<IStorage> remote = new() { CallBase = true };

        remote.Object.TryTouch("anything.jpg").Should().BeFalse();
    }
}
