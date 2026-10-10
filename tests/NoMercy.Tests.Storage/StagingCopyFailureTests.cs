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
using NoMercy.Tests.Storage.Fakes;

namespace NoMercy.Tests.Storage;

[Trait("Category", "Unit")]
public sealed class StagingCopyFailureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Driver_failed_or_cancelled_copy_deletes_partial_file(bool cancel)
    {
        using CancellationTokenSource cancellation = new();
        string? stagedPath = null;
        HashSet<string> before = Directory
            .GetFiles(StoragePaths.TempRoot, "nomercy-probe-*.audit250")
            .ToHashSet();
        InMemoryStorageDriver driver = new()
        {
            IsolatedReadStreamFactory = () =>
                new PartialReadStream(() =>
                {
                    stagedPath = FindNewFile("nomercy-probe-*.audit250", before);
                    if (cancel)
                        cancellation.Cancel();
                }),
        };

        try
        {
            Exception? error = await Record.ExceptionAsync(() =>
                ((IStorageDriver)driver).AcquireLocalPathAsync("clip.audit250", cancellation.Token)
            );
            AssertFailure(error, cancel);
            stagedPath.Should().NotBeNull();
            File.Exists(stagedPath).Should().BeFalse("a failed stage must remove its partial file");
        }
        finally
        {
            DeleteIfPresent(stagedPath);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Remote_failed_or_cancelled_copy_deletes_partial_file(bool cancel)
    {
        using CancellationTokenSource cancellation = new();
        string? stagedPath = null;
        HashSet<string> before = Directory
            .GetFiles(StoragePaths.TempRoot, "nomercy-remote-*")
            .ToHashSet();
        InMemoryStorageDriver driver = new()
        {
            IsolatedReadStreamFactory = () =>
                new PartialReadStream(() =>
                {
                    stagedPath = FindNewFile("nomercy-remote-*", before);
                    if (cancel)
                        cancellation.Cancel();
                }),
        };
        RemoteStorage storage = new(driver);

        try
        {
            Exception? error = await Record.ExceptionAsync(() =>
                storage.AcquireLocalPathAsync("clip", cancellation.Token)
            );
            AssertFailure(error, cancel);
            stagedPath.Should().NotBeNull();
            File.Exists(stagedPath).Should().BeFalse("a failed stage must remove its partial file");
        }
        finally
        {
            DeleteIfPresent(stagedPath);
        }
    }

    [Fact]
    public void Remote_sync_failed_copy_deletes_partial_file()
    {
        string? stagedPath = null;
        HashSet<string> before = Directory
            .GetFiles(StoragePaths.TempRoot, "nomercy-remote-*")
            .ToHashSet();
        InMemoryStorageDriver driver = new()
        {
            IsolatedReadStreamFactory = () =>
                new PartialReadStream(() => stagedPath = FindNewFile("nomercy-remote-*", before)),
        };
        RemoteStorage storage = new(driver);

        try
        {
            Action act = () => storage.AcquireLocalPath("clip");
            act.Should().Throw<IOException>();
            stagedPath.Should().NotBeNull();
            File.Exists(stagedPath).Should().BeFalse("a failed stage must remove its partial file");
        }
        finally
        {
            DeleteIfPresent(stagedPath);
        }
    }

    private static void AssertFailure(Exception? error, bool cancel)
    {
        if (cancel)
            error.Should().BeAssignableTo<OperationCanceledException>();
        else
            error.Should().BeOfType<IOException>();
    }

    private static string FindNewFile(string pattern, HashSet<string> before) =>
        Directory.GetFiles(StoragePaths.TempRoot, pattern).Single(path => !before.Contains(path));

    private static void DeleteIfPresent(string? path)
    {
        if (path is not null && File.Exists(path))
            File.Delete(path);
    }

    private sealed class PartialReadStream(Action onFirstRead) : MemoryStream([1, 2])
    {
        private bool _readOnce;

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_readOnce)
                throw new IOException("source connection dropped");

            _readOnce = true;
            int count = base.Read(buffer.Span[..1]);
            onFirstRead();
            return ValueTask.FromResult(count);
        }

        public override int Read(Span<byte> buffer)
        {
            if (_readOnce)
                throw new IOException("source connection dropped");

            _readOnce = true;
            int count = base.Read(buffer[..1]);
            onFirstRead();
            return count;
        }

        public override void CopyTo(Stream destination, int bufferSize)
        {
            destination.WriteByte(1);
            onFirstRead();
            throw new IOException("source connection dropped");
        }
    }
}
