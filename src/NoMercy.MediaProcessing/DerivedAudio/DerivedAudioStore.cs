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

using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NoMercy.Database;
using NoMercy.Storage;
using DerivedAudioRow = NoMercy.Database.Models.Music.DerivedAudio;

namespace NoMercy.MediaProcessing.DerivedAudio;

/// <inheritdoc />
public sealed class DerivedAudioStore : IDerivedAudioStore
{
    private const string TempFolder = "tmp";

    private readonly IStorage _storage;
    private readonly IDbContextFactory<MediaContext> _contextFactory;
    private readonly ILogger<DerivedAudioStore> _logger;

    // Serializes concurrent PutAsync calls for the same key within this store
    // instance. The store is registered as a process-wide singleton, so two
    // jobs producing the same stem or rendered segment racing through PutAsync
    // is the expected case, not an edge case. One SemaphoreSlim accumulates
    // per distinct key for the store's lifetime — acceptable because the key
    // space is bounded by distinct content ever produced, which is small next
    // to a media library.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    /// <param name="storage">An <see cref="IStorage" /> scoped to <c>AppFiles.DerivedAudioPath</c>; every path below is relative to it.</param>
    /// <param name="contextFactory">Creates a fresh <see cref="MediaContext" /> per operation.</param>
    /// <param name="logger">Reports how many bytes eviction frees.</param>
    public DerivedAudioStore(
        IStorage storage,
        IDbContextFactory<MediaContext> contextFactory,
        ILogger<DerivedAudioStore> logger
    )
    {
        _storage = storage;
        _contextFactory = contextFactory;
        _logger = logger;
    }

    public string RelativePath(string key) => $"{key[..2]}/{key}";

    public async Task<DerivedAudioEntry> PutAsync(
        Stream content,
        string contentType,
        CancellationToken ct = default
    )
    {
        await _storage.CreateDirectoryAsync(TempFolder, ct);
        string tempPath = $"{TempFolder}/{Ulid.NewUlid()}";
        long bytes = 0;
        string key;

        await using (Stream target = await _storage.OpenWriteAsync(tempPath, true, ct))
        using (IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            byte[] buffer = new byte[81920];
            int read;
            while ((read = await content.ReadAsync(buffer, ct)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
                bytes += read;
            }
            key = Convert.ToHexStringLower(hash.GetHashAndReset());
        }

        SemaphoreSlim keyLock = _locks.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await keyLock.WaitAsync(ct);
        try
        {
            return await StoreAndRegisterAsync(tempPath, key, contentType, bytes, ct);
        }
        finally
        {
            keyLock.Release();
        }
    }

    // Split out of PutAsync so the per-key lock covers only the exists/move/
    // register sequence, not the hashing above it. Even with the lock, a
    // second store instance (a second process, or a second DI resolution
    // that is not actually a singleton) can still race here, so both the
    // move and the insert additionally treat "someone else already did this"
    // as success rather than letting the caller fail: the content is stored
    // and registered either way, which is the contract PutAsync promises.
    private async Task<DerivedAudioEntry> StoreAndRegisterAsync(
        string tempPath,
        string key,
        string contentType,
        long bytes,
        CancellationToken ct
    )
    {
        string finalPath = RelativePath(key);
        bool tempPending = true;
        try
        {
            if (await _storage.ExistsAsync(finalPath, ct))
            {
                // Another PutAsync call already landed this exact content under this key.
            }
            else
            {
                await _storage.CreateDirectoryAsync(key[..2], ct);
                try
                {
                    await _storage.MoveAsync(tempPath, finalPath, ct);
                    tempPending = false;
                }
                catch (IOException)
                {
                    // await is not allowed in an exception filter, so the
                    // "did someone else already win this race" check happens
                    // here instead: if the destination exists, that is exactly
                    // what happened and the loser can carry on; anything else
                    // is a real I/O failure and must not be swallowed.
                    if (!await _storage.ExistsAsync(finalPath, ct))
                    {
                        throw;
                    }
                    // Lost a cross-instance race to move the same content into
                    // place; the winner's file is already there.
                }
            }
        }
        finally
        {
            // A crash here (or the losing branch above) would otherwise leave
            // tempPath behind forever; EvictAsync also sweeps tmp/ as a backstop.
            if (tempPending && await _storage.ExistsAsync(tempPath, ct))
            {
                await _storage.DeleteAsync(tempPath, ct);
            }
        }

        await using MediaContext context = await _contextFactory.CreateDbContextAsync(ct);
        DerivedAudioRow? existing = await context.DerivedAudio.FirstOrDefaultAsync(
            row => row.Key == key,
            ct
        );
        if (existing is null)
        {
            DateTime now = DateTime.UtcNow;
            context.DerivedAudio.Add(
                new DerivedAudioRow
                {
                    Key = key,
                    ContentType = contentType,
                    Bytes = bytes,
                    CreatedAt = now,
                    LastUsedAt = now,
                }
            );
            try
            {
                await context.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // Lost a cross-instance race to register the row; the winner's
                // row is already there — bump it instead of failing the caller.
                await TouchAsync(key, ct);
            }
        }
        else
        {
            await TouchAsync(key, ct);
        }

        return new DerivedAudioEntry(key, contentType, bytes);
    }

    public Task<bool> ExistsAsync(string key, CancellationToken ct = default) =>
        _storage.ExistsAsync(RelativePath(key), ct);

    public async Task<Stream?> OpenReadAsync(string key, CancellationToken ct = default)
    {
        if (!await _storage.ExistsAsync(RelativePath(key), ct))
        {
            return null;
        }
        await TouchAsync(key, ct);
        return await _storage.OpenReadAsync(RelativePath(key), ct);
    }

    public async Task TouchAsync(string key, CancellationToken ct = default)
    {
        await using MediaContext context = await _contextFactory.CreateDbContextAsync(ct);
        await context
            .DerivedAudio.Where(row => row.Key == key)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.LastUsedAt, DateTime.UtcNow), ct);
    }

    public async Task DeleteAsync(string key, CancellationToken ct = default)
    {
        if (await _storage.ExistsAsync(RelativePath(key), ct))
        {
            await _storage.DeleteAsync(RelativePath(key), ct);
        }
        await using MediaContext context = await _contextFactory.CreateDbContextAsync(ct);
        await context.DerivedAudio.Where(row => row.Key == key).ExecuteDeleteAsync(ct);
    }

    public async Task<long> EvictAsync(
        long capBytes,
        TimeSpan grace,
        CancellationToken ct = default
    )
    {
        List<DerivedAudioRow> rows;
        await using (MediaContext context = await _contextFactory.CreateDbContextAsync(ct))
        {
            rows = await context.DerivedAudio.AsNoTracking().ToListAsync(ct);
        }

        long freed = 0;
        foreach (
            DerivedAudioRow row in DerivedAudioEviction.Choose(
                rows,
                capBytes,
                grace,
                DateTime.UtcNow
            )
        )
        {
            ct.ThrowIfCancellationRequested();
            await DeleteAsync(row.Key, ct);
            freed += row.Bytes;
        }

        await SweepStaleTempFilesAsync(grace, ct);

        if (freed > 0)
        {
            _logger.LogInformation("Derived audio eviction freed {Freed} bytes", freed);
        }
        return freed;
    }

    // A crash between the temp write in PutAsync and the move into place is
    // the only thing that should ever leave a file under tmp/ — nothing else
    // ever revisits that folder, so it needs its own sweep rather than relying
    // on the register-row policy above, which never sees these files at all.
    private async Task SweepStaleTempFilesAsync(TimeSpan grace, CancellationToken ct)
    {
        if (!await _storage.ExistsAsync(TempFolder, ct))
        {
            return;
        }

        DateTimeOffset cutoff = DateTimeOffset.UtcNow - grace;
        await foreach (StorageEntry entry in _storage.ListAsync(TempFolder, null, false, ct))
        {
            ct.ThrowIfCancellationRequested();
            if (entry.IsDirectory || entry.LastModified > cutoff)
            {
                continue;
            }
            await _storage.DeleteAsync(entry.Path, ct);
        }
    }
}
