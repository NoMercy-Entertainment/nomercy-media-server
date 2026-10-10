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

using System.Runtime.InteropServices;
using NoMercy.Storage.Drivers.Nfs.Interop;

namespace NoMercy.Storage.Drivers.Nfs;

/// <summary>
/// Write-only <see cref="Stream"/> over a libnfs file handle.
/// Buffers in 1 MiB chunks and flushes via <c>nfs_write</c>.
/// The handle is closed (implicitly synced) on <see cref="Dispose"/>.
/// Every native call is gated on the driver's lock — the libnfs context is
/// shared across streams and is not re-entrant.
/// </summary>
internal sealed class NfsWriteStream : Stream
{
    // One native write per chunk. libnfs clamps a larger request to the mount's
    // negotiated wsize internally and the loop below advances on the short
    // write, so at 1 MiB a 5 GB file trades ~163k lock+pin cycles for ~5k. The
    // default is set from the throughput sweep; the ctor accepts an override so
    // the sweep can measure the curve.
    private const int DefaultChunkSize = 1024 * 1024;

    private readonly int _chunkSize;
    private readonly IntPtr _nfs;
    private readonly IntPtr _fh;
    private readonly SemaphoreSlim _lock;
    private readonly ILibNfs _libNfs;
    private bool _disposed;

    internal NfsWriteStream(IntPtr nfs, IntPtr fh, SemaphoreSlim driverLock)
        : this(nfs, fh, driverLock, LibNfsPInvoke.Instance) { }

    internal NfsWriteStream(
        IntPtr nfs,
        IntPtr fh,
        SemaphoreSlim driverLock,
        ILibNfs libNfs,
        int chunkSize = DefaultChunkSize
    )
    {
        _nfs = nfs;
        _fh = fh;
        _lock = driverLock;
        _libNfs = libNfs;
        _chunkSize = chunkSize;
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        int written = 0;
        while (written < count)
        {
            int chunk = Math.Min(_chunkSize, count - written);
            IntPtr pinned = Marshal.AllocHGlobal(chunk);
            try
            {
                Marshal.Copy(buffer, offset + written, pinned, chunk);

                int n;
                string? err = null;
                try
                {
                    _lock.Wait();
                    try
                    {
                        n = _libNfs.Write(_nfs, _fh, pinned, chunk);
                        if (n < 0)
                            err = _libNfs.GetError(_nfs);
                    }
                    finally
                    {
                        _lock.Release();
                    }
                }
                catch (ObjectDisposedException)
                {
                    throw new IOException("NFS driver disposed during write");
                }

                if (n < 0)
                    throw new IOException($"NFS write failed: {err}");
                if (n == 0)
                    throw new IOException("NFS write failed: libnfs wrote zero bytes");
                written += n;
            }
            finally
            {
                Marshal.FreeHGlobal(pinned);
            }
        }
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        byte[] tmp = buffer.ToArray();
        Write(tmp, 0, tmp.Length);
    }

    public override Task WriteAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken
    )
    {
        Write(buffer, offset, count);
        return Task.CompletedTask;
    }

    public override ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default
    )
    {
        Write(buffer.Span);
        return ValueTask.CompletedTask;
    }

    public override void Flush() { }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (_disposed)
            return;
        _disposed = true;

        try
        {
            _lock.Wait();
            try
            {
                _libNfs.Close(_nfs, _fh);
            }
            finally
            {
                _lock.Release();
            }
        }
        catch (ObjectDisposedException)
        {
            // Driver was disposed first — its own Dispose will tear down the
            // context, so the fh is already gone. Nothing to do.
        }

        base.Dispose(disposing);
    }
}
