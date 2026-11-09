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

using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NoMercy.Api.Middleware;
using NoMercy.NmSystem.Monitoring;
using NoMercy.Storage;
using Xunit;

namespace NoMercy.Tests.Api.Middleware;

[Trait("Category", "Middleware")]
public sealed class PlaybackFileCacheTests
{
    private sealed class FakeFiles
    {
        public Dictionary<string, (byte[] Bytes, DateTimeOffset Modified)> Files { get; } = [];
        public Dictionary<string, int> Opens { get; } = [];

        public void Put(string path, string text, DateTimeOffset modified) =>
            Files[path] = (Encoding.UTF8.GetBytes(text), modified);

        public int OpenCount(string path) => Opens.GetValueOrDefault(path);

        public Mock<IStorage> Storage()
        {
            Mock<IStorage> storage = new();
            storage.Setup(s => s.Exists(It.IsAny<string>())).Returns<string>(Files.ContainsKey);
            storage
                .Setup(s => s.Size(It.IsAny<string>()))
                .Returns<string>(p => Files[p].Bytes.Length);
            storage
                .Setup(s => s.LastModified(It.IsAny<string>()))
                .Returns<string>(p => Files[p].Modified);
            storage
                .Setup(s => s.OpenRead(It.IsAny<string>()))
                .Returns<string>(p =>
                {
                    lock (Opens)
                        Opens[p] = OpenCount(p) + 1;
                    return new MemoryStream(Files[p].Bytes);
                });
            storage
                .Setup(s =>
                    s.TryGetPresignedUrlAsync(
                        It.IsAny<string>(),
                        It.IsAny<TimeSpan>(),
                        It.IsAny<CancellationToken>()
                    )
                )
                .ReturnsAsync((Uri?)null);
            return storage;
        }
    }

    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string Master =
        "#EXTM3U\n"
        + "#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"a\",URI=\"audio_eng/audio.m3u8\"\n"
        + "#EXT-X-STREAM-INF:BANDWIDTH=1000,AUDIO=\"a\"\n"
        + "video_1080p/video.m3u8\n";

    private static async Task<(int Status, string Body)> Get(
        DynamicStaticFilesMiddleware middleware,
        IStorage storage,
        Ulid folderId,
        string path,
        string? range = null
    )
    {
        DefaultHttpContext context = new();
        context.Request.Path = $"/{folderId}/{path}";
        context.Response.Body = new MemoryStream();
        if (range is not null)
            context.Request.Headers.Range = range;

        Mock<IStorageFactory> factory = new();
        factory
            .Setup(f => f.For(It.IsAny<Ulid>(), It.IsAny<Ulid>(), It.IsAny<string>()))
            .Returns(storage);

        await middleware.InvokeAsync(context, factory.Object, new MediaActivityMonitor());

        context.Response.Body.Position = 0;
        string body = new StreamReader(context.Response.Body).ReadToEnd();
        return (context.Response.StatusCode, body);
    }

    private static (
        DynamicStaticFilesMiddleware Middleware,
        ServedFolderRegistry Registry,
        Ulid Folder
    ) Create()
    {
        ServedFolderRegistry registry = new();
        Ulid folder = Ulid.NewUlid();
        registry.Add(folder, Ulid.NewUlid(), string.Empty);
        DynamicStaticFilesMiddleware middleware = new(
            _ => Task.CompletedTask,
            registry,
            NullLogger<DynamicStaticFilesMiddleware>.Instance,
            new PlaybackFileCache()
        );
        return (middleware, registry, folder);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (int i = 0; i < 100 && !condition(); i++)
            await Task.Delay(20);
    }

    [Fact]
    public async Task A_repeat_read_of_an_unchanged_playlist_does_not_open_the_file_again()
    {
        FakeFiles files = new();
        files.Put("show/video.m3u8", "#EXTM3U\nseg0.ts\n", T0);
        (DynamicStaticFilesMiddleware middleware, _, Ulid folder) = Create();
        IStorage storage = files.Storage().Object;

        (int _, string first) = await Get(middleware, storage, folder, "show/video.m3u8");
        (int status, string second) = await Get(middleware, storage, folder, "show/video.m3u8");

        files.OpenCount("show/video.m3u8").Should().Be(1);
        status.Should().Be(200);
        second.Should().Be(first).And.Be("#EXTM3U\nseg0.ts\n");
    }

    [Fact]
    public async Task A_playlist_rewritten_with_the_same_size_is_read_again()
    {
        FakeFiles files = new();
        files.Put("show/video.m3u8", "AAAA", T0);
        (DynamicStaticFilesMiddleware middleware, _, Ulid folder) = Create();
        IStorage storage = files.Storage().Object;
        await Get(middleware, storage, folder, "show/video.m3u8");

        files.Put("show/video.m3u8", "BBBB", T0.AddSeconds(5));
        (int _, string body) = await Get(middleware, storage, folder, "show/video.m3u8");

        body.Should().Be("BBBB");
        files.OpenCount("show/video.m3u8").Should().Be(2);
    }

    [Fact]
    public async Task A_playlist_that_changed_size_is_read_again()
    {
        FakeFiles files = new();
        files.Put("show/video.m3u8", "AAAA", T0);
        (DynamicStaticFilesMiddleware middleware, _, Ulid folder) = Create();
        IStorage storage = files.Storage().Object;
        await Get(middleware, storage, folder, "show/video.m3u8");

        files.Put("show/video.m3u8", "AAAAAAAA", T0);
        (int _, string body) = await Get(middleware, storage, folder, "show/video.m3u8");

        body.Should().Be("AAAAAAAA");
    }

    [Fact]
    public async Task A_range_request_on_a_cached_playlist_is_answered_from_the_cache_with_206()
    {
        FakeFiles files = new();
        files.Put("show/video.m3u8", "0123456789", T0);
        (DynamicStaticFilesMiddleware middleware, _, Ulid folder) = Create();
        IStorage storage = files.Storage().Object;
        await Get(middleware, storage, folder, "show/video.m3u8");

        (int status, string body) = await Get(
            middleware,
            storage,
            folder,
            "show/video.m3u8",
            "bytes=2-5"
        );

        status.Should().Be(206);
        body.Should().Be("2345");
        files.OpenCount("show/video.m3u8").Should().Be(1);
    }

    [Fact]
    public async Task Media_segments_are_never_cached()
    {
        FakeFiles files = new();
        files.Put("show/seg0.ts", "segment", T0);
        (DynamicStaticFilesMiddleware middleware, _, Ulid folder) = Create();
        IStorage storage = files.Storage().Object;

        await Get(middleware, storage, folder, "show/seg0.ts");
        await Get(middleware, storage, folder, "show/seg0.ts");

        files.OpenCount("show/seg0.ts").Should().Be(2);
    }

    [Fact]
    public async Task Serving_a_master_warms_the_playlists_it_names()
    {
        FakeFiles files = new();
        files.Put("show/master.m3u8", Master, T0);
        files.Put("show/video_1080p/video.m3u8", "#EXTM3U\nv0.ts\n", T0);
        files.Put("show/audio_eng/audio.m3u8", "#EXTM3U\na0.ts\n", T0);
        (DynamicStaticFilesMiddleware middleware, _, Ulid folder) = Create();
        IStorage storage = files.Storage().Object;

        await Get(middleware, storage, folder, "show/master.m3u8");
        await WaitUntil(() =>
            files.OpenCount("show/video_1080p/video.m3u8") == 1
            && files.OpenCount("show/audio_eng/audio.m3u8") == 1
        );
        (int _, string video) = await Get(
            middleware,
            storage,
            folder,
            "show/video_1080p/video.m3u8"
        );

        video.Should().Be("#EXTM3U\nv0.ts\n");
        files.OpenCount("show/video_1080p/video.m3u8").Should().Be(1);
        files.OpenCount("show/audio_eng/audio.m3u8").Should().Be(1);
    }

    [Fact]
    public async Task Warming_playlists_that_are_already_cached_opens_nothing()
    {
        FakeFiles files = new();
        files.Put("show/master.m3u8", Master, T0);
        files.Put("show/video_1080p/video.m3u8", "v", T0);
        files.Put("show/audio_eng/audio.m3u8", "a", T0);
        IStorage storage = files.Storage().Object;
        PlaybackFileCache cache = new();
        Ulid folder = Ulid.NewUlid();
        await cache.WarmReferencedPlaylistsAsync(
            storage,
            folder,
            "show/master.m3u8",
            Encoding.UTF8.GetBytes(Master)
        );

        await cache.WarmReferencedPlaylistsAsync(
            storage,
            folder,
            "show/master.m3u8",
            Encoding.UTF8.GetBytes(Master)
        );

        files.OpenCount("show/video_1080p/video.m3u8").Should().Be(1);
        files.OpenCount("show/audio_eng/audio.m3u8").Should().Be(1);
    }

    [Fact]
    public async Task Warming_a_master_that_names_a_missing_playlist_does_not_throw()
    {
        FakeFiles files = new();
        files.Put("show/master.m3u8", Master, T0);
        IStorage storage = files.Storage().Object;

        Func<Task> warm = () =>
            new PlaybackFileCache().WarmReferencedPlaylistsAsync(
                storage,
                Ulid.NewUlid(),
                "show/master.m3u8",
                Encoding.UTF8.GetBytes(Master)
            );

        await warm.Should().NotThrowAsync();
    }

    [Fact]
    public void Referenced_playlists_skip_segments_urls_and_paths_that_leave_the_folder()
    {
        const string text =
            "#EXTM3U\n#EXT-X-MEDIA:URI=\"audio/a.m3u8?x=1\"\n"
            + "seg0.ts\nhttps://example.test/other.m3u8\n/abs.m3u8\n../up.m3u8\nok/video.m3u8\n";

        List<string> found = PlaybackFileCache.ReferencedPlaylists("lib/show/master.m3u8", text);

        found.Should().Equal("lib/show/audio/a.m3u8", "lib/show/ok/video.m3u8");
    }

    [Fact]
    public void The_cache_never_holds_more_than_its_byte_budget()
    {
        FakeFiles files = new();
        string big = new('x', (int)PlaybackFileCache.MaxFileBytes);
        for (int i = 0; i < 100; i++)
            files.Put($"f{i}.m3u8", big, T0);
        IStorage storage = files.Storage().Object;
        PlaybackFileCache cache = new();
        Ulid folder = Ulid.NewUlid();

        for (int i = 0; i < 100; i++)
            cache.Load(storage, folder, $"f{i}.m3u8");

        cache
            .Count.Should()
            .BeLessThanOrEqualTo(
                (int)(PlaybackFileCache.MaxTotalBytes / PlaybackFileCache.MaxFileBytes)
            );
    }

    [Fact]
    public void A_file_over_the_size_cap_is_not_cached()
    {
        FakeFiles files = new();
        files.Put("huge.m3u8", new string('x', (int)PlaybackFileCache.MaxFileBytes + 1), T0);
        PlaybackFileCache cache = new();

        byte[]? bytes = cache.Load(files.Storage().Object, Ulid.NewUlid(), "huge.m3u8");

        bytes.Should().BeNull();
        cache.Count.Should().Be(0);
    }
}
