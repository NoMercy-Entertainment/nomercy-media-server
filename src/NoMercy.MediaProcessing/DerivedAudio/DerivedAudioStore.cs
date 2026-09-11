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

    // Serializes puts, touches and deletes of one key inside this store, which
    // is a process-wide singleton. A semaphore is only ever created for a key
    // the register actually holds - an unknown key is answered before this is
    // touched - so the dictionary is bounded by stored content, not by what
    // callers ask about.
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

    // Split out so the per-key lock covers only the exists/move/register
    // sequence, not the hashing. A second store instance can still race here,
    // so both the move and the insert treat "already done" as success.
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
                await TouchRowAsync(key, ct);
            }
        }
        else
        {
            await TouchRowAsync(key, ct);
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

    /// <summary>
    /// The pre-check outside the lock is what keeps a key nothing ever stored
    /// from minting a semaphore; the re-check inside it is what survives an
    /// eviction that took the entry while this call was queued behind it.
    /// <para>
    /// Only the stream outlives the lock. That is safe: the touch above it has
    /// already moved <c>LastUsedAt</c> inside the grace window, so the next
    /// eviction's own re-check under this same lock finds the key warm and
    /// leaves it alone while the caller is still reading.
    /// </para>
    /// </summary>
    public async Task<Stream?> OpenReadAsync(string key, CancellationToken ct = default)
    {
        if (!DerivedAudioKey.IsValid(key))
        {
            return null;
        }

        if (!await HasRegisterRowAsync(key, ct))
        {
            return null;
        }

        if (!await _storage.ExistsAsync(RelativePath(key), ct))
        {
            return null;
        }

        await RunAfterPreCheckAsync();

        return await UnderKeyLockAsync<Stream?>(
            key,
            async () =>
            {
                if (!await HasRegisterRowAsync(key, ct))
                {
                    return null;
                }

                if (!await _storage.ExistsAsync(RelativePath(key), ct))
                {
                    return null;
                }

                await TouchRowAsync(key, ct);
                return await _storage.OpenReadAsync(RelativePath(key), ct);
            },
            ct
        );
    }

    /// <summary>
    /// Both halves of the same guard as <see cref="OpenReadAsync" />: the
    /// cheap pre-check keeps unknown keys out of the lock dictionary, and the
    /// re-check under the lock makes sure the row a touch is about to bump is
    /// still there after the wait.
    /// </summary>
    public async Task TouchAsync(string key, CancellationToken ct = default)
    {
        if (!DerivedAudioKey.IsValid(key) || !await HasRegisterRowAsync(key, ct))
        {
            return;
        }

        await RunAfterPreCheckAsync();

        await UnderKeyLockAsync(
            key,
            async () =>
            {
                if (!await HasRegisterRowAsync(key, ct))
                {
                    return;
                }

                await TouchRowAsync(key, ct);
            },
            ct
        );
    }

    /// <summary>
    /// Runs after the pre-check outside the lock and before the lock itself,
    /// so a test can land an eviction in exactly the window the re-check under
    /// the lock guards against. Never set in production.
    /// </summary>
    internal Func<Task>? AfterPreCheck { get; init; }

    private Task RunAfterPreCheckAsync() =>
        AfterPreCheck is null ? Task.CompletedTask : AfterPreCheck();

    /// <summary>
    /// A key with no register row is nothing to delete: a content file without
    /// one is the half-written state a crash leaves behind, and the orphan
    /// sweep in <see cref="EvictAsync" /> is what frees that.
    /// </summary>
    public async Task DeleteAsync(string key, CancellationToken ct = default)
    {
        if (!DerivedAudioKey.IsValid(key) || !await HasRegisterRowAsync(key, ct))
        {
            return;
        }

        await UnderKeyLockAsync(key, () => DeleteEntryAsync(key, ct), ct);
    }

    /// <summary>
    /// Asked before the key's semaphore is created, so a well-formed key
    /// nothing ever stored leaves no lock behind in the dictionary.
    /// </summary>
    private async Task<bool> HasRegisterRowAsync(string key, CancellationToken ct)
    {
        await using MediaContext context = await _contextFactory.CreateDbContextAsync(ct);
        return await context.DerivedAudio.AsNoTracking().AnyAsync(row => row.Key == key, ct);
    }

    /// <summary>
    /// Runs between choosing a victim and deleting it, so a test can make a
    /// key busy in exactly the window this method guards against. Never set
    /// in production.
    /// </summary>
    internal Func<Task>? BeforeDelete { get; init; }

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

            if (BeforeDelete is not null)
            {
                await BeforeDelete();
            }

            // A victim that turned out to be in use again is left alone, which
            // can leave the store over its cap until the next run.
            freed += await DeleteIfStillColdAsync(row.Key, grace, ct) ?? 0;
        }

        await SweepStaleTempFilesAsync(grace, ct);
        await SweepOrphanedContentFilesAsync(grace, ct);

        if (freed > 0)
        {
            _logger.LogInformation("Derived audio eviction freed {Freed} bytes", freed);
        }
        return freed;
    }

    /// <summary>
    /// Deletes one victim under the same per-key lock a put takes, and only
    /// after re-reading its row: eviction chose from a snapshot, and a key
    /// read or touched since then is in use again - deleting it would pull the
    /// file out from under an ffmpeg run that is already reading it. Null when
    /// the key was left alone, the bytes freed when it went.
    /// </summary>
    private async Task<long?> DeleteIfStillColdAsync(
        string key,
        TimeSpan grace,
        CancellationToken ct
    )
    {
        SemaphoreSlim keyLock = _locks.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await keyLock.WaitAsync(ct);
        try
        {
            DerivedAudioRow? row;
            await using (MediaContext context = await _contextFactory.CreateDbContextAsync(ct))
            {
                row = await context
                    .DerivedAudio.AsNoTracking()
                    .FirstOrDefaultAsync(candidate => candidate.Key == key, ct);
            }

            if (row is null || row.LastUsedAt > DateTime.UtcNow - grace)
            {
                return null;
            }

            await DeleteEntryAsync(key, ct);
            return row.Bytes;
        }
        finally
        {
            keyLock.Release();
        }
    }

    /// <summary>
    /// The per-key lock <see cref="PutAsync" /> takes, around a touch or a
    /// delete: a touch that lands while eviction is deleting the same key
    /// leaves a register row pointing at a file that is already gone.
    /// </summary>
    private Task UnderKeyLockAsync(string key, Func<Task> body, CancellationToken ct) =>
        UnderKeyLockAsync<bool>(
            key,
            async () =>
            {
                await body();
                return true;
            },
            ct
        );

    /// <summary>The same lock around a body that answers with something.</summary>
    private async Task<T> UnderKeyLockAsync<T>(string key, Func<Task<T>> body, CancellationToken ct)
    {
        SemaphoreSlim keyLock = _locks.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await keyLock.WaitAsync(ct);
        try
        {
            return await body();
        }
        finally
        {
            keyLock.Release();
        }
    }

    /// <summary>The touch itself, with the key's lock already held.</summary>
    private async Task TouchRowAsync(string key, CancellationToken ct)
    {
        await using MediaContext context = await _contextFactory.CreateDbContextAsync(ct);
        await context
            .DerivedAudio.Where(row => row.Key == key)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.LastUsedAt, DateTime.UtcNow), ct);
    }

    /// <summary>The delete itself, with the key's lock already held.</summary>
    private async Task DeleteEntryAsync(string key, CancellationToken ct)
    {
        if (await _storage.ExistsAsync(RelativePath(key), ct))
        {
            await _storage.DeleteAsync(RelativePath(key), ct);
        }

        await using MediaContext context = await _contextFactory.CreateDbContextAsync(ct);
        await context.DerivedAudio.Where(row => row.Key == key).ExecuteDeleteAsync(ct);
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
