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

    /// <summary>
    /// The only member that throws on a bad key rather than answering "not
    /// found": it returns a path, so it has no way to say "there is none".
    /// Every other member below treats an invalid key as an absent one, which
    /// is what a plugin holding a stale or invented key should see.
    /// </summary>
    /// <exception cref="ArgumentException">The key is not a lowercase hex SHA-256 digest.</exception>
    public string RelativePath(string key)
    {
        if (!DerivedAudioKey.IsValid(key))
        {
            throw new ArgumentException(
                "A derived-audio key is 64 lowercase hex characters.",
                nameof(key)
            );
        }

        return $"{key[..2]}/{key}";
    }

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

    /// <summary>
    /// True only when both halves of the entry are there: the file AND its
    /// register row. A file without a row is what a crash between the move and
    /// the insert leaves behind - handing that key back as present would give
    /// a caller a file eviction never counts and a foreign key rejects.
    /// </summary>
    public async Task<bool> ExistsAsync(string key, CancellationToken ct = default)
    {
        if (!DerivedAudioKey.IsValid(key))
        {
            return false;
        }

        if (!await _storage.ExistsAsync(RelativePath(key), ct))
        {
            return false;
        }

        await using MediaContext context = await _contextFactory.CreateDbContextAsync(ct);
        return await context.DerivedAudio.AsNoTracking().AnyAsync(row => row.Key == key, ct);
    }

    public async Task<Stream?> OpenReadAsync(string key, CancellationToken ct = default)
    {
        if (!DerivedAudioKey.IsValid(key))
        {
            return null;
        }

        if (!await _storage.ExistsAsync(RelativePath(key), ct))
        {
            return null;
        }
        await TouchAsync(key, ct);
        return await _storage.OpenReadAsync(RelativePath(key), ct);
    }

    public async Task TouchAsync(string key, CancellationToken ct = default)
    {
        if (!DerivedAudioKey.IsValid(key))
        {
            return;
        }

        await using MediaContext context = await _contextFactory.CreateDbContextAsync(ct);
        await context
            .DerivedAudio.Where(row => row.Key == key)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.LastUsedAt, DateTime.UtcNow), ct);
    }

    public async Task DeleteAsync(string key, CancellationToken ct = default)
    {
        if (!DerivedAudioKey.IsValid(key))
        {
            return;
        }

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
        await SweepOrphanedContentFilesAsync(grace, ct);

        if (freed > 0)
        {
            _logger.LogInformation("Derived audio eviction freed {Freed} bytes", freed);
        }
        return freed;
    }

    // The mirror of the tmp/ sweep, one step further along: a crash between
    // the move into place and the register insert leaves a content file whose
    // row never landed. Nothing addresses it - ExistsAsync answers false for a
    // file without a row - so only this sweep will ever free it. The grace
    // window is what keeps it from racing a PutAsync that is between its own
    // move and insert right now.
    private async Task SweepOrphanedContentFilesAsync(TimeSpan grace, CancellationToken ct)
    {
        HashSet<string> knownKeys;
        await using (MediaContext context = await _contextFactory.CreateDbContextAsync(ct))
        {
            knownKeys = (
                await context.DerivedAudio.AsNoTracking().Select(row => row.Key).ToListAsync(ct)
            ).ToHashSet(StringComparer.Ordinal);
        }

        DateTimeOffset cutoff = DateTimeOffset.UtcNow - grace;

        List<string> contentFolders = [];
        await foreach (StorageEntry entry in _storage.ListAsync(string.Empty, null, false, ct))
        {
            ct.ThrowIfCancellationRequested();
            string name = entry.Path.Split('/')[^1];
            if (entry.IsDirectory && name.Length == 2 && name != TempFolder)
            {
                contentFolders.Add(entry.Path);
            }
        }

        foreach (string folder in contentFolders)
        {
            await foreach (StorageEntry entry in _storage.ListAsync(folder, null, false, ct))
            {
                ct.ThrowIfCancellationRequested();
                string name = entry.Path.Split('/')[^1];
                if (entry.IsDirectory || entry.LastModified > cutoff || knownKeys.Contains(name))
                {
                    continue;
                }

                _logger.LogInformation(
                    "Derived audio: removing content file {Key} that has no register row",
                    name
                );
                await _storage.DeleteAsync(entry.Path, ct);
            }
        }
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
