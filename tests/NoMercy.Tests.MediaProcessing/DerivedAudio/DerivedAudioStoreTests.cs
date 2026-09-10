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
using System.Text;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NoMercy.Database;
using NoMercy.MediaProcessing.DerivedAudio;
using NoMercy.Storage;
using NoMercy.Storage.Drivers.Local;
using NoMercy.Storage.Validation;

namespace NoMercy.Tests.MediaProcessing.DerivedAudio;

// This test namespace shares its last segment with NoMercy.Database.Models.Music.DerivedAudio,
// but this file only ever reaches that type through NoMercy.Database.MediaContext.DerivedAudio
// (the DbSet), never the bare type name, so no alias is needed here.
[Trait("Category", "Unit")]
public sealed class DerivedAudioStoreTests : IDisposable
{
    private readonly string _root;
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MediaContext> _options;
    private readonly IDbContextFactory<MediaContext> _contextFactory;

    public DerivedAudioStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"nm-derived-audio-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);

        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<MediaContext>().UseSqlite(_connection).Options;
        using (MediaContext context = new(_options))
        {
            context.Database.EnsureCreated();
        }

        Mock<IDbContextFactory<MediaContext>> factory = new();
        factory
            .Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MediaContext(_options));
        factory.Setup(f => f.CreateDbContext()).Returns(() => new MediaContext(_options));
        _contextFactory = factory.Object;
    }

    public void Dispose()
    {
        _connection.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup; a locked handle on Windows must not fail the test run.
        }
        GC.SuppressFinalize(this);
    }

    private IDerivedAudioStore Store()
    {
        LocalStorageDriver driver = new();
        StoragePathGuard guard = new([_root], driver);
        IStorage storage = new LocalStorage(driver, guard);
        return new DerivedAudioStore(
            storage,
            _contextFactory,
            NullLogger<DerivedAudioStore>.Instance
        );
    }

    [Fact]
    public async Task Put_StoresUnderItsSha256_AndRegistersTheRow()
    {
        byte[] content = Encoding.UTF8.GetBytes("stem bytes");
        string expectedKey = Convert.ToHexStringLower(SHA256.HashData(content));

        DerivedAudioEntry entry = await Store().PutAsync(new MemoryStream(content), "audio/opus");

        entry.Key.Should().Be(expectedKey);
        entry.Bytes.Should().Be(content.Length);
        (await Store().ExistsAsync(entry.Key)).Should().BeTrue();
        Store().RelativePath(entry.Key).Should().Be($"{expectedKey[..2]}/{expectedKey}");
        await using MediaContext read = new(_options);
        (await read.DerivedAudio.AsNoTracking().SingleAsync()).Bytes.Should().Be(content.Length);
    }

    [Fact]
    public async Task PutTwice_IsOneFileAndOneRow()
    {
        byte[] content = Encoding.UTF8.GetBytes("the same bytes, put twice");
        IDerivedAudioStore store = Store();

        DerivedAudioEntry first = await store.PutAsync(new MemoryStream(content), "audio/opus");
        DerivedAudioEntry second = await store.PutAsync(new MemoryStream(content), "audio/opus");

        second.Key.Should().Be(first.Key);
        await using MediaContext read = new(_options);
        (await read.DerivedAudio.AsNoTracking().CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task OpenRead_ReturnsTheBytes_AndBumpsLastUsedAt()
    {
        byte[] content = Encoding.UTF8.GetBytes("read this back");
        IDerivedAudioStore store = Store();
        DerivedAudioEntry entry = await store.PutAsync(new MemoryStream(content), "audio/opus");

        DateTime before;
        await using (MediaContext read = new(_options))
        {
            before = (await read.DerivedAudio.AsNoTracking().SingleAsync()).LastUsedAt;
        }

        await Task.Delay(20);

        await using Stream? stream = await store.OpenReadAsync(entry.Key);
        stream.Should().NotBeNull();
        using MemoryStream buffer = new();
        await stream!.CopyToAsync(buffer);
        buffer.ToArray().Should().Equal(content);

        await using MediaContext read2 = new(_options);
        DateTime after = (await read2.DerivedAudio.AsNoTracking().SingleAsync()).LastUsedAt;
        after.Should().BeAfter(before);
    }

    [Fact]
    public async Task OpenRead_OfAnUnknownKey_IsNull()
    {
        Stream? stream = await Store().OpenReadAsync(new string('a', 64));

        stream.Should().BeNull();
    }

    [Fact]
    public async Task Delete_RemovesFileAndRow()
    {
        byte[] content = Encoding.UTF8.GetBytes("delete this");
        IDerivedAudioStore store = Store();
        DerivedAudioEntry entry = await store.PutAsync(new MemoryStream(content), "audio/opus");

        await store.DeleteAsync(entry.Key);

        (await store.ExistsAsync(entry.Key)).Should().BeFalse();
        await using MediaContext read = new(_options);
        (await read.DerivedAudio.AsNoTracking().AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Evict_RemovesTheChosenFilesAndRows_AndReportsBytesFreed()
    {
        // Three puts with LastUsedAt set back by hand (3, 2, 1 days); cap = 2 files' worth.
        // Expect: the oldest is gone from disk and from the register, freed == its size.
        IDerivedAudioStore store = Store();
        DerivedAudioEntry oldest = await store.PutAsync(
            new MemoryStream(Encoding.UTF8.GetBytes("oldest content")),
            "audio/opus"
        );
        DerivedAudioEntry middle = await store.PutAsync(
            new MemoryStream(Encoding.UTF8.GetBytes("middle content")),
            "audio/opus"
        );
        DerivedAudioEntry newest = await store.PutAsync(
            new MemoryStream(Encoding.UTF8.GetBytes("newest content")),
            "audio/opus"
        );

        DateTime now = DateTime.UtcNow;
        await using (MediaContext context = new(_options))
        {
            await context
                .DerivedAudio.Where(row => row.Key == oldest.Key)
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.LastUsedAt, now.AddDays(-3)));
            await context
                .DerivedAudio.Where(row => row.Key == middle.Key)
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.LastUsedAt, now.AddDays(-2)));
            await context
                .DerivedAudio.Where(row => row.Key == newest.Key)
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.LastUsedAt, now.AddDays(-1)));
        }

        long capBytes = 2 * oldest.Bytes;
        long freed = await store.EvictAsync(capBytes, TimeSpan.FromHours(1));

        freed.Should().Be(oldest.Bytes);
        (await store.ExistsAsync(oldest.Key)).Should().BeFalse();
        (await store.ExistsAsync(middle.Key)).Should().BeTrue();
        (await store.ExistsAsync(newest.Key)).Should().BeTrue();
        await using MediaContext read = new(_options);
        (await read.DerivedAudio.AsNoTracking().CountAsync()).Should().Be(2);
    }
}
