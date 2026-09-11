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

using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NoMercy.Database;
using NoMercy.Storage;
using NoMercy.Storage.Common;
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
    // is a process-wide singleton. A key is only ever locked once the register
    // is known to hold it - an unknown key is answered before this is touched -
    // so the lock table is bounded by stored content, not by what callers ask
    // about.
    private readonly KeyedAsyncLock _locks = new();

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

        using IDisposable keyLock = await _locks.AcquireAsync(key, ct);
        return await StoreAndRegisterAsync(tempPath, key, contentType, bytes, ct);
    }

    /// <summary>
    /// Split out so the per-key lock covers only the exists/move/register
    /// sequence, not the hashing. A second store instance can still race here,
    /// so both the move and the insert treat "already done" as success.
    /// <para>
    /// That is the content-addressed half of the server's two rules for a lost
    /// write. A key here IS the bytes: whoever won the race wrote exactly what
    /// this call was going to write, so there is nothing to reconcile and
    /// nothing for the caller to redo. The loser bumps the winner's row and
    /// reports success.
    /// </para>
    /// <para>
    /// The other half is the keyed one, in
    /// <c>PluginMusicAnalysisWriter.ResolveFailedDjWriteAsync</c> and
    /// <c>ResolveFailedStemWriteAsync</c>: a row addressed by an identifier
    /// rather than by its content may hold something else entirely, so the
    /// loser re-reads it and either accepts an identical row or refuses. Both
    /// halves agree on the third case - a failure with no competing row is a
    /// real error, logged and named, never dressed up as a retry.
    /// </para>
    /// </summary>
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
                // Lost a cross-instance race to register the row. The key is
                // the hash of the content, so the winner's row describes the
                // same bytes this call just wrote — bump it instead of failing
                // the caller. See the summary above for why the keyed writes
                // in PluginMusicAnalysisWriter cannot assume that.
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
    /// from taking a lock at all; the re-check inside it is what survives an
    /// eviction that took the entry while this call was queued behind it.
    /// <para>
    /// Only the stream outlives the lock. That is safe: the touch above it has
    /// already moved <c>LastUsedAt</c> inside the grace window, so the next
    /// eviction's own re-check under this same lock finds the key warm and
    /// leaves it alone while the caller is still reading.
    /// </para>
    /// <para>
    /// Two database round-trips, not three: the touch under the lock is also
    /// the re-check, because an update that matches no row says the entry went
    /// while this call was queued behind the key. The pre-check outside the
    /// lock stays - it is what keeps a key nothing ever stored from minting a
    /// lock entry at all.
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
                if (await TouchRowAsync(key, ct) == 0)
                {
                    return null;
                }

                if (!await _storage.ExistsAsync(RelativePath(key), ct))
                {
                    return null;
                }

                return await _storage.OpenReadAsync(RelativePath(key), ct);
            },
            ct
        );
    }

    /// <inheritdoc />
    /// <remarks>
    /// Both halves of the same guard as <see cref="OpenReadAsync" />: the
    /// cheap pre-check keeps unknown keys out of the lock dictionary, and the
    /// update under the lock is its own re-check - an affected-row count of
    /// zero means the row this touch was about to bump went while the call was
    /// waiting for the key.
    /// <para>
    /// The file is asked about after the row, not before it: the row is the
    /// half eviction removes first, and the cheaper of the two to find gone.
    /// </para>
    /// </remarks>
    public async Task<bool> TouchAsync(string key, CancellationToken ct = default)
    {
        if (!DerivedAudioKey.IsValid(key) || !await HasRegisterRowAsync(key, ct))
        {
            return false;
        }

        await RunAfterPreCheckAsync();

        return await UnderKeyLockAsync(
            key,
            async () =>
                await TouchRowAsync(key, ct) != 0
                && await _storage.ExistsAsync(RelativePath(key), ct),
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

        await RunAfterPreCheckAsync();

        await UnderKeyLockAsync<bool>(key, () => DeleteEntryAsync(key, ct), ct);
    }

    /// <summary>
    /// Asked before the key is ever locked, so a well-formed key nothing ever
    /// stored leaves no entry behind in the lock table.
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
        using IDisposable keyLock = await _locks.AcquireAsync(key, ct);

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

        return await DeleteEntryAsync(key, ct) ? row.Bytes : null;
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
        using IDisposable keyLock = await _locks.AcquireAsync(key, ct);
        return await body();
    }

    /// <summary>
    /// The touch itself, with the key's lock already held. Answers how many
    /// register rows it moved, which is the re-check every caller under the
    /// lock needs: zero means the entry went while the caller was waiting.
    /// </summary>
    private async Task<int> TouchRowAsync(string key, CancellationToken ct)
    {
        await using MediaContext context = await _contextFactory.CreateDbContextAsync(ct);
        return await context
            .DerivedAudio.Where(row => row.Key == key)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.LastUsedAt, DateTime.UtcNow), ct);
    }

    /// <summary>
    /// The delete itself, with the key's lock already held. The register row
    /// goes first and its affected-row count decides the file: zero means
    /// eviction or another delete already claimed this key, so the file under
    /// it is no longer this call's to remove. True when the entry went.
    /// </summary>
    private async Task<bool> DeleteEntryAsync(string key, CancellationToken ct)
    {
        int deleted;
        await using (MediaContext context = await _contextFactory.CreateDbContextAsync(ct))
        {
            deleted = await context
                .DerivedAudio.Where(row => row.Key == key)
                .ExecuteDeleteAsync(ct);
        }

        if (deleted == 0)
        {
            return false;
        }

        if (await _storage.ExistsAsync(RelativePath(key), ct))
        {
            await _storage.DeleteAsync(RelativePath(key), ct);
        }

        return true;
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
            // The length check alone excludes tmp/: a shard is the first two
            // characters of a key, and "tmp" is three. Whatever is in there
            // belongs to SweepStaleTempFilesAsync, which has its own rules.
            if (entry.IsDirectory && name.Length == 2)
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
