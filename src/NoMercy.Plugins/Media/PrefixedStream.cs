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

namespace NoMercy.PluginSdk.Media;

/// <summary>
/// A stream that first gives back bytes already read from it, then the rest.
/// <para>
/// The media proxy looks at the first bytes of an upstream answer to tell a
/// playlist from a stream. When it is a stream, those bytes still belong to
/// the viewer. Putting them back in front of the rest, rather than buffering
/// the answer, keeps a live stream that never ends from filling memory.
/// </para>
/// </summary>
internal sealed class PrefixedStream(byte[] prefix, int prefixLength, Stream rest) : Stream
{
    private int _served;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int fromPrefix = FromPrefix(buffer);

        return fromPrefix > 0 ? fromPrefix : rest.Read(buffer);
    }

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken
    ) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default
    )
    {
        int fromPrefix = FromPrefix(buffer.Span);

        return fromPrefix > 0
            ? ValueTask.FromResult(fromPrefix)
            : rest.ReadAsync(buffer, cancellationToken);
    }

    public override void Flush() { }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            rest.Dispose();

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await rest.DisposeAsync();
        await base.DisposeAsync();
    }

    private int FromPrefix(Span<byte> buffer)
    {
        int count = Math.Min(prefixLength - _served, buffer.Length);

        if (count <= 0)
            return 0;

        prefix.AsSpan(_served, count).CopyTo(buffer);
        _served += count;

        return count;
    }
}
