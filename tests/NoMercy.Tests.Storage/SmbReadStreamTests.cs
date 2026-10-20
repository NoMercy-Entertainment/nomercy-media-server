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

using NoMercy.Storage.Drivers.Smb;
using SMBLibrary;
using SMBLibrary.Client;

namespace NoMercy.Tests.Storage;

/// <summary>
/// <see cref="SmbReadStream"/> pulls chunks via <c>ReadFile</c> at an
/// advancing server offset and carries over any bytes the caller's buffer
/// couldn't hold. These tests demand: carry-over correctness across small
/// reads, EOF detection, error propagation, and that Dispose always closes
/// the handle and tears down the session even when CloseFile fails.
/// </summary>
[Trait("Category", "Unit")]
public sealed class SmbReadStreamTests
{
    private static SmbSession NewSession(Mock<ISMBFileStore> store) =>
        new() { Client = new SMB2Client(), Store = store.Object };

    /// <summary>
    /// Moq's out-parameter matching only supports a fixed value per Setup, so
    /// a stateful sequence (first call returns data, second call returns EOF)
    /// needs Moq's delegate-based <c>Returns</c> overload instead.
    /// </summary>
    private delegate NTStatus InvokeReadFile(
        out byte[] data,
        object handle,
        long offset,
        int maxCount
    );

    [Fact]
    public void Read_returns_bytes_then_zero_at_eof()
    {
        byte[] content = [1, 2, 3, 4, 5];
        Mock<ISMBFileStore> store = new();
        int callIndex = 0;
        store
            .Setup(s =>
                s.ReadFile(
                    out It.Ref<byte[]>.IsAny,
                    It.IsAny<object>(),
                    It.IsAny<long>(),
                    It.IsAny<int>()
                )
            )
            .Returns(
                (InvokeReadFile)(
                    (out byte[] data, object _, long _, int _) =>
                    {
                        if (callIndex++ == 0)
                        {
                            data = content;
                            return NTStatus.STATUS_SUCCESS;
                        }
                        data = [];
                        return NTStatus.STATUS_END_OF_FILE;
                    }
                )
            );
        SmbSession session = NewSession(store);
        using SmbReadStream stream = new(session, new object(), "/file.bin");

        byte[] buffer = new byte[content.Length];
        int read = stream.Read(buffer, 0, buffer.Length);
        int readAtEof = stream.Read(buffer, 0, buffer.Length);

        read.Should().Be(content.Length);
        buffer.Should().Equal(content);
        readAtEof
            .Should()
            .Be(0, "a second read past EOF must return 0, not re-issue ReadFile forever");
    }

    [Fact]
    public void Read_with_small_buffer_drains_carry_over_across_multiple_calls()
    {
        byte[] content = [1, 2, 3, 4, 5, 6];
        Mock<ISMBFileStore> store = new();
        int callIndex = 0;
        store
            .Setup(s =>
                s.ReadFile(
                    out It.Ref<byte[]>.IsAny,
                    It.IsAny<object>(),
                    It.IsAny<long>(),
                    It.IsAny<int>()
                )
            )
            .Returns(
                (InvokeReadFile)(
                    (out byte[] data, object _, long _, int _) =>
                    {
                        if (callIndex++ == 0)
                        {
                            data = content;
                            return NTStatus.STATUS_SUCCESS;
                        }
                        data = [];
                        return NTStatus.STATUS_END_OF_FILE;
                    }
                )
            );
        SmbSession session = NewSession(store);
        using SmbReadStream stream = new(session, new object(), "/file.bin");

        byte[] small = new byte[2];
        List<byte> collected = [];
        int n;
        while ((n = stream.Read(small, 0, small.Length)) > 0)
            collected.AddRange([.. small.AsSpan(0, n)]);

        collected
            .Should()
            .Equal(content, "carry-over must reassemble the full chunk across undersized reads");
        store.Verify(
            s =>
                s.ReadFile(
                    out It.Ref<byte[]>.IsAny,
                    It.IsAny<object>(),
                    It.IsAny<long>(),
                    It.IsAny<int>()
                ),
            Times.Exactly(2),
            "only one native ReadFile should be needed to serve a chunk that fits, plus one to detect EOF"
        );
    }

    [Fact]
    public void Read_with_zero_count_returns_zero_without_calling_ReadFile()
    {
        Mock<ISMBFileStore> store = new();
        SmbSession session = NewSession(store);
        using SmbReadStream stream = new(session, new object(), "/file.bin");

        stream.Read([], 0, 0).Should().Be(0);
        store.Verify(
            s =>
                s.ReadFile(
                    out It.Ref<byte[]>.IsAny,
                    It.IsAny<object>(),
                    It.IsAny<long>(),
                    It.IsAny<int>()
                ),
            Times.Never
        );
    }

    [Fact]
    public void Read_propagates_non_success_non_eof_status_as_IOException()
    {
        // A failure status must throw whether or not the server attached data.
        // This is the chunk-with-error shape; the null-buffer shape SMBLibrary
        // actually produces on failure is covered below.
        Mock<ISMBFileStore> store = new();
        byte[] nonEmptyChunk = [0x01];
        store
            .Setup(s =>
                s.ReadFile(out nonEmptyChunk, It.IsAny<object>(), It.IsAny<long>(), It.IsAny<int>())
            )
            .Returns(NTStatus.STATUS_ACCESS_DENIED);
        SmbSession session = NewSession(store);
        using SmbReadStream stream = new(session, new object(), "/secret.bin");

        Action act = () => _ = stream.Read(new byte[4], 0, 4);

        act.Should().Throw<IOException>().WithMessage("*STATUS_ACCESS_DENIED*");
    }

    [Fact]
    public void Read_throws_IOException_when_ReadFile_fails_without_returning_data()
    {
        // SMBLibrary assigns the buffer only on STATUS_SUCCESS, so every failed
        // read arrives as a null chunk. Treating that as end of file let a
        // dropped session finish a copy with a short destination file.
        Mock<ISMBFileStore> store = new();
        store
            .Setup(s =>
                s.ReadFile(
                    out It.Ref<byte[]>.IsAny,
                    It.IsAny<object>(),
                    It.IsAny<long>(),
                    It.IsAny<int>()
                )
            )
            .Returns(
                (InvokeReadFile)(
                    (out byte[] data, object _, long _, int _) =>
                    {
                        data = null!;
                        return NTStatus.STATUS_USER_SESSION_DELETED;
                    }
                )
            );
        SmbSession session = NewSession(store);
        using SmbReadStream stream = new(session, new object(), "/movie.mkv");

        Action act = () => _ = stream.Read(new byte[4], 0, 4);

        act.Should().Throw<IOException>().WithMessage("*STATUS_USER_SESSION_DELETED*");
    }

    [Fact]
    public void Read_treats_a_successful_empty_chunk_as_end_of_file()
    {
        Mock<ISMBFileStore> store = new();
        store
            .Setup(s =>
                s.ReadFile(
                    out It.Ref<byte[]>.IsAny,
                    It.IsAny<object>(),
                    It.IsAny<long>(),
                    It.IsAny<int>()
                )
            )
            .Returns(
                (InvokeReadFile)(
                    (out byte[] data, object _, long _, int _) =>
                    {
                        data = [];
                        return NTStatus.STATUS_SUCCESS;
                    }
                )
            );
        SmbSession session = NewSession(store);
        using SmbReadStream stream = new(session, new object(), "/empty.bin");

        int read = stream.Read(new byte[4], 0, 4);
        int readAgain = stream.Read(new byte[4], 0, 4);

        read.Should().Be(0);
        readAgain.Should().Be(0);
        store.Verify(
            s =>
                s.ReadFile(
                    out It.Ref<byte[]>.IsAny,
                    It.IsAny<object>(),
                    It.IsAny<long>(),
                    It.IsAny<int>()
                ),
            Times.Once,
            "an empty successful chunk marks EOF, so the stream must not ask the server again"
        );
    }

    [Fact]
    public async Task ReadAsync_byte_array_overload_delegates_to_Read()
    {
        byte[] content = [7, 8, 9];
        Mock<ISMBFileStore> store = new();
        int callIndex = 0;
        store
            .Setup(s =>
                s.ReadFile(
                    out It.Ref<byte[]>.IsAny,
                    It.IsAny<object>(),
                    It.IsAny<long>(),
                    It.IsAny<int>()
                )
            )
            .Returns(
                (InvokeReadFile)(
                    (out byte[] data, object _, long _, int _) =>
                    {
                        if (callIndex++ == 0)
                        {
                            data = content;
                            return NTStatus.STATUS_SUCCESS;
                        }
                        data = [];
                        return NTStatus.STATUS_END_OF_FILE;
                    }
                )
            );
        SmbSession session = NewSession(store);
        using SmbReadStream stream = new(session, new object(), "/file.bin");

        byte[] buffer = new byte[3];
        int read = await stream.ReadAsync(buffer, 0, 3, CancellationToken.None);

        read.Should().Be(3);
        buffer.Should().Equal(content);
    }

    [Fact]
    public async Task ReadAsync_byte_array_overload_honors_cancellation()
    {
        Mock<ISMBFileStore> store = new();
        SmbSession session = NewSession(store);
        using SmbReadStream stream = new(session, new object(), "/file.bin");
        using CancellationTokenSource cts = new();
        cts.Cancel();

        Func<Task> act = async () => _ = await stream.ReadAsync(new byte[4], 0, 4, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ReadAsync_memory_overload_delegates_to_Read()
    {
        byte[] content = [3, 2, 1];
        Mock<ISMBFileStore> store = new();
        int callIndex = 0;
        store
            .Setup(s =>
                s.ReadFile(
                    out It.Ref<byte[]>.IsAny,
                    It.IsAny<object>(),
                    It.IsAny<long>(),
                    It.IsAny<int>()
                )
            )
            .Returns(
                (InvokeReadFile)(
                    (out byte[] data, object _, long _, int _) =>
                    {
                        if (callIndex++ == 0)
                        {
                            data = content;
                            return NTStatus.STATUS_SUCCESS;
                        }
                        data = [];
                        return NTStatus.STATUS_END_OF_FILE;
                    }
                )
            );
        SmbSession session = NewSession(store);
        using SmbReadStream stream = new(session, new object(), "/file.bin");

        byte[] buffer = new byte[3];
        int read = await stream.ReadAsync(buffer.AsMemory());

        read.Should().Be(3);
        buffer.Should().Equal(content);
    }

    [Fact]
    public void ReadAsync_memory_overload_honors_cancellation()
    {
        Mock<ISMBFileStore> store = new();
        SmbSession session = NewSession(store);
        using SmbReadStream stream = new(session, new object(), "/file.bin");
        using CancellationTokenSource cts = new();
        cts.Cancel();

        Func<Task> act = async () => _ = await stream.ReadAsync(new byte[4].AsMemory(), cts.Token);

        act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void Capability_flags_and_unsupported_members_match_a_forward_only_stream()
    {
        Mock<ISMBFileStore> store = new();
        SmbSession session = NewSession(store);
        using SmbReadStream stream = new(session, new object(), "/file.bin");

        stream.CanRead.Should().BeTrue();
        stream.CanSeek.Should().BeFalse();
        stream.CanWrite.Should().BeFalse();

        ((Action)(() => _ = stream.Length)).Should().Throw<NotSupportedException>();
        ((Action)(() => _ = stream.Position)).Should().Throw<NotSupportedException>();
        ((Action)(() => stream.Position = 0)).Should().Throw<NotSupportedException>();
        ((Action)(() => stream.Seek(0, SeekOrigin.Begin))).Should().Throw<NotSupportedException>();
        ((Action)(() => stream.SetLength(1))).Should().Throw<NotSupportedException>();
        ((Action)(() => stream.Write([1], 0, 1))).Should().Throw<NotSupportedException>();
        ((Action)(() => stream.Flush())).Should().NotThrow();
    }

    [Fact]
    public void Dispose_closes_the_handle_and_disposes_the_session()
    {
        Mock<ISMBFileStore> store = new();
        store.Setup(s => s.CloseFile(It.IsAny<object>())).Returns(NTStatus.STATUS_SUCCESS);
        store.Setup(s => s.Disconnect()).Returns(NTStatus.STATUS_SUCCESS);
        SmbSession session = NewSession(store);
        object handle = new();
        SmbReadStream stream = new(session, handle, "/file.bin");

        stream.Dispose();

        store.Verify(s => s.CloseFile(handle), Times.Once);
        store.Verify(
            s => s.Disconnect(),
            Times.Once,
            "the session must be torn down after the handle closes"
        );
    }

    [Fact]
    public void Dispose_disposes_the_session_even_when_CloseFile_throws()
    {
        Mock<ISMBFileStore> store = new();
        store.Setup(s => s.CloseFile(It.IsAny<object>())).Throws<InvalidOperationException>();
        store.Setup(s => s.Disconnect()).Returns(NTStatus.STATUS_SUCCESS);
        SmbSession session = NewSession(store);
        SmbReadStream stream = new(session, new object(), "/file.bin");

        Action act = () => stream.Dispose();

        act.Should()
            .Throw<InvalidOperationException>(
                "CloseFile failures are not swallowed by the stream itself"
            );
        store.Verify(
            s => s.Disconnect(),
            Times.Once,
            "the session must still be disposed via `finally` even though CloseFile threw"
        );
    }

    [Fact]
    public void Dispose_is_idempotent()
    {
        Mock<ISMBFileStore> store = new();
        store.Setup(s => s.CloseFile(It.IsAny<object>())).Returns(NTStatus.STATUS_SUCCESS);
        store.Setup(s => s.Disconnect()).Returns(NTStatus.STATUS_SUCCESS);
        SmbSession session = NewSession(store);
        SmbReadStream stream = new(session, new object(), "/file.bin");

        stream.Dispose();
        stream.Dispose();

        store.Verify(
            s => s.CloseFile(It.IsAny<object>()),
            Times.Once,
            "a second Dispose must not re-close the handle"
        );
    }
}
