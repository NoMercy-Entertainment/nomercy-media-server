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

using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.PluginSdk.Quotas;

/// <summary>
/// The plugin's own folder, counted.
/// <para>
/// Counted here because this is where the server hands the bytes over, which
/// is the one place the number is a fact. A quota worked out by walking the
/// folder afterwards is a quota that has already been passed.
/// </para>
/// </summary>
public class PluginMeteredStorageScope(
    IPluginStorageScope inner,
    Ulid pluginId,
    PluginQuotaMeter meter
) : IPluginStorageScope
{
    public PluginStorageLocation? Location => inner.Location;

    public Task<bool> ExistsAsync(string path, CancellationToken ct = default) =>
        inner.ExistsAsync(path, ct);

    public Task<Stream> OpenReadAsync(string path, CancellationToken ct = default) =>
        inner.OpenReadAsync(path, ct);

    public async Task<Stream> OpenWriteAsync(
        string path,
        bool overwrite,
        CancellationToken ct = default
    )
    {
        // Overwriting gives back what was there before it takes the new bytes,
        // so rewriting one file every minute does not read as a plugin filling
        // a disk.
        if (overwrite)
            meter.ReleaseDisk(pluginId, await SizeAsync(path, ct));

        return new PluginDiskMeteredStream(
            await inner.OpenWriteAsync(path, overwrite, ct),
            pluginId,
            meter
        );
    }

    public async Task DeleteAsync(string path, CancellationToken ct = default)
    {
        long freed = await SizeAsync(path, ct);

        await inner.DeleteAsync(path, ct);

        meter.ReleaseDisk(pluginId, freed);
    }

    public IAsyncEnumerable<PluginStorageEntry> ListAsync(
        string path,
        bool recursive = false,
        CancellationToken ct = default
    ) => inner.ListAsync(path, recursive, ct);

    /// <summary>
    /// What one path is holding right now, a folder included. Read from the
    /// listing rather than remembered, because a plugin's folder survives a
    /// restart and anything remembered in memory does not.
    /// </summary>
    private async Task<long> SizeAsync(string path, CancellationToken ct)
    {
        if (!await inner.ExistsAsync(path, ct))
            return 0;

        long total = 0;
        string wanted = path.Replace('\\', '/').TrimStart('/');

        // Listed from the parent, because listing a file gives nothing: a
        // scope lists what is inside a folder, and a file has no inside. The
        // entry for the file itself is in its parent's listing.
        int slash = wanted.LastIndexOf('/');
        string parent = slash < 0 ? string.Empty : wanted[..slash];

        await foreach (PluginStorageEntry entry in inner.ListAsync(parent, false, ct))
        {
            if (entry.Path != wanted)
                continue;

            if (!entry.IsDirectory)
                return entry.SizeBytes;

            await foreach (PluginStorageEntry inside in inner.ListAsync(wanted, true, ct))
                total += inside.SizeBytes;

            return total;
        }

        return total;
    }
}

/// <summary>Counts every byte as it is written, and refuses the one that goes past.</summary>
public class PluginDiskMeteredStream(Stream inner, Ulid pluginId, PluginQuotaMeter meter) : Stream
{
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => throw new NotSupportedException();
    }

    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default
    )
    {
        Account(buffer.Length);

        await inner.WriteAsync(buffer, cancellationToken);
    }

    public override Task WriteAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken
    ) => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Write(byte[] buffer, int offset, int count)
    {
        Account(count);

        inner.Write(buffer, offset, count);
    }

    public override void Flush() => inner.Flush();

    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

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

    /// <summary>
    /// Refused before the bytes land, which is what makes it a ceiling rather
    /// than a report. The write throws, so the plugin's own error path runs
    /// instead of it carrying on with a file it thinks it wrote.
    /// </summary>
    private void Account(int bytes)
    {
        if (meter.AccountDisk(pluginId, bytes) is { } refusal)
            throw new PluginRefusedException(refusal);
    }
}
