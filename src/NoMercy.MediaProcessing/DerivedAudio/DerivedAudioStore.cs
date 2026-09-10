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
using DerivedAudioRow = NoMercy.Database.Models.Music.DerivedAudio;

namespace NoMercy.MediaProcessing.DerivedAudio;

/// <inheritdoc />
public sealed class DerivedAudioStore : IDerivedAudioStore
{
    private const string TempFolder = "tmp";

    private readonly IStorage _storage;
    private readonly IDbContextFactory<MediaContext> _contextFactory;
    private readonly ILogger<DerivedAudioStore> _logger;

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

        string finalPath = RelativePath(key);
        if (await _storage.ExistsAsync(finalPath, ct))
        {
            await _storage.DeleteAsync(tempPath, ct);
        }
        else
        {
            await _storage.CreateDirectoryAsync(key[..2], ct);
            await _storage.MoveAsync(tempPath, finalPath, ct);
        }

        await using MediaContext context = await _contextFactory.CreateDbContextAsync(ct);
        DerivedAudioRow? existing = await context.DerivedAudio.FirstOrDefaultAsync(
            row => row.Key == key,
            ct
        );
        DateTime now = DateTime.UtcNow;
        if (existing is null)
        {
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
        }
        else
        {
            existing.LastUsedAt = now;
        }
        await context.SaveChangesAsync(ct);

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

        if (freed > 0)
        {
            _logger.LogInformation("Derived audio eviction freed {Freed} bytes", freed);
        }
        return freed;
    }
}
