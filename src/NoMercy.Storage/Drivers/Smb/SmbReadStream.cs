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

using SMBLibrary;

namespace NoMercy.Storage.Drivers.Smb;

/// <summary>
/// Read stream for the SMB driver. Pulls bytes from the share with <c>ReadFile</c>
/// at an advancing offset over its own dedicated SMB connection and open handle,
/// so a multi-GB read never buffers the whole file in memory. Server chunks that
/// overflow the caller's buffer are held in a small carry-over until the next
/// read. The connection is held for the stream's lifetime and closed on dispose.
/// </summary>
internal sealed class SmbReadStream : Stream
{
    // Per-request read size. The SMB2 client caps a single ReadFile at the
    // negotiated MaxReadSize; requesting more than that is clamped there, so this
    // trades managed round-trips for larger ones up to that ceiling. Default set
    // from the throughput sweep; the ctor accepts an override so the sweep can
    // measure the curve.
    private const int DefaultChunkSize = 1024 * 1024;

    private readonly int _chunkSize;
    private readonly SmbSession _session;
    private readonly object _handle;
    private readonly string _path;
    private long _serverOffset;
    private byte[] _carry = [];
    private int _carryPos;
    private bool _eof;
    private bool _disposed;

    internal SmbReadStream(
        SmbSession session,
        object handle,
        string path,
        int chunkSize = DefaultChunkSize
    )
    {
        _session = session;
        _handle = handle;
        _path = path;
        _chunkSize = chunkSize;
    }

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
        if (count == 0)
            return 0;

        // Serve from carry-over first.
        if (_carryPos < _carry.Length)
            return DrainCarry(buffer, offset, count);

        if (_eof)
            return 0;

        NTStatus st = _session.Store.ReadFile(out byte[] chunk, _handle, _serverOffset, _chunkSize);
        if (st == NTStatus.STATUS_END_OF_FILE)
        {
            _eof = true;
            return 0;
        }
        // SMBLibrary only fills the buffer on success, so a failed read looks
        // like an empty one; the status must be judged first or a dropped
        // session ends the stream as if the file were complete.
        SmbStatus.EnsureSuccess(st, $"read '{_path}'");
        if (chunk is null || chunk.Length == 0)
        {
            _eof = true;
            return 0;
        }
        _serverOffset += chunk.Length;
        _carry = chunk;
        _carryPos = 0;
        return DrainCarry(buffer, offset, count);
    }

    public override async Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken ct
    )
    {
        ct.ThrowIfCancellationRequested();
        return await Task.FromResult(Read(buffer, offset, count));
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        byte[] tmp = new byte[buffer.Length];
        int read = Read(tmp, 0, tmp.Length);
        tmp.AsSpan(0, read).CopyTo(buffer.Span);
        return ValueTask.FromResult(read);
    }

    private int DrainCarry(byte[] buffer, int offset, int count)
    {
        int available = _carry.Length - _carryPos;
        int take = Math.Min(available, count);
        Array.Copy(_carry, _carryPos, buffer, offset, take);
        _carryPos += take;
        return take;
    }

    public override void Flush() { }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (_disposed)
            return;
        _disposed = true;
        if (disposing)
        {
            try
            {
                _session.Store.CloseFile(_handle);
            }
            finally
            {
                _session.Dispose();
            }
        }
        base.Dispose(disposing);
    }
}
