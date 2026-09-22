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

namespace NoMercy.PluginSdk.Quotas;

/// <summary>
/// Counts every chunk on its way to a client and waits when the plugin is
/// sending faster than it may.
/// <para>
/// Waiting rather than failing is the whole point of a rate: the viewer keeps
/// watching, a little behind, instead of seeing playback end. A total would
/// have to stop; a rate only has to slow down.
/// </para>
/// </summary>
public class PluginUploadMeteredStream(
    Stream inner,
    Ulid pluginId,
    PluginQuotaMeter meter,
    TimeProvider clock
) : Stream
{
    /// <summary>
    /// How long an over-budget chunk waits before the next one is read. Short
    /// enough that a client's buffer survives it, long enough that a second's
    /// worth of chunks cannot all leave inside that second.
    /// </summary>
    public static TimeSpan Pause { get; } = TimeSpan.FromMilliseconds(250);

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default
    )
    {
        int read = await inner.ReadAsync(buffer, cancellationToken);

        if (read > 0 && meter.AccountUpload(pluginId, read) is not null)
            await Task.Delay(Pause, clock, cancellationToken);

        return read;
    }

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken
    ) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

    public override void Flush() => inner.Flush();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            inner.Dispose();

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync();
        await base.DisposeAsync();

        GC.SuppressFinalize(this);
    }
}
