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

using System.Net;
using System.Net.Sockets;

namespace NoMercy.Plugins.Abstractions;

/// <summary>
/// A datagram socket read and written as a stream.
/// <para>
/// A reply goes back to the peer the last datagram came from, which is what a
/// tracker or a DHT node expects. Nothing is reassembled: one read is one
/// datagram, and a buffer smaller than the datagram loses the rest of it, the
/// same as the socket underneath.
/// </para>
/// </summary>
public sealed class PluginDatagramStream(UdpClient client) : Stream
{
    private IPEndPoint? _lastSender;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default
    )
    {
        UdpReceiveResult received = await client.ReceiveAsync(cancellationToken);
        _lastSender = received.RemoteEndPoint;

        int length = Math.Min(buffer.Length, received.Buffer.Length);
        received.Buffer.AsMemory(0, length).CopyTo(buffer);

        return length;
    }

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken
    ) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default
    )
    {
        if (_lastSender is null)
            await client.SendAsync(buffer, cancellationToken);
        else
            await client.SendAsync(buffer, _lastSender, cancellationToken);
    }

    public override Task WriteAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken
    ) => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override void Flush() { }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
}
