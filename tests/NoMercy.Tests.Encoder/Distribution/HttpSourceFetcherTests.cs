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

using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.Encoder.Composition;
using NoMercy.Encoder.Distribution;
using NoMercy.Tests.Encoder.Storage;

namespace NoMercy.Tests.Encoder.Distribution;

public class HttpSourceFetcherTests
{
    [Fact]
    public async Task EnsureLocalAsync_SourceExistsLocally_ReturnsPathUnchanged()
    {
        // Fast path: worker sees the source on its own filesystem
        // (shared NAS). No download, no tempdir write.
        string tempDir = CreateTempDir();
        try
        {
            string sourcePath = Path.Combine(tempDir, "source.mkv");
            await File.WriteAllBytesAsync(sourcePath, [0x00, 0x01, 0x02]);

            HttpSourceFetcher sut = MakeFetcher(tempDir, out _);
            EncodeTask task = MakeTask(sourcePath);

            string local = await sut.EnsureLocalAsync(task, CancellationToken.None);

            local.Should().Be(sourcePath);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task EnsureLocalAsync_RemoteSource_DownloadsToCache()
    {
        // Source doesn't exist locally → fetcher hits the coordinator,
        // writes the response body to the cache directory, returns the
        // cached path.
        string tempDir = CreateTempDir();
        try
        {
            byte[] expectedBytes = Encoding.UTF8.GetBytes("remote-source-bytes");
            HttpSourceFetcher sut = MakeFetcher(tempDir, out FakeHandler handler);
            handler.RespondWith(HttpStatusCode.OK, expectedBytes);

            EncodeTask task = MakeTask("/nonexistent/source.mkv", taskId: "t-dl-1");
            string local = await sut.EnsureLocalAsync(task, CancellationToken.None);

            local.Should().NotBe(task.InputPath);
            File.Exists(local).Should().BeTrue("downloaded file must land on disk");
            (await File.ReadAllBytesAsync(local))
                .Should()
                .BeEquivalentTo(expectedBytes, "downloaded bytes must match the response body");

            handler.Requests.Should().HaveCount(1);
            handler.Requests[0].Query.Should().Contain("path=");
            handler.Requests[0].Query.Should().Contain("sig=");
            handler.Requests[0].Query.Should().Contain("ts=");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task EnsureLocalAsync_Retry_ReusesCachedDownload()
    {
        // Second EnsureLocalAsync for the same task should not re-download
        // — the cached file from the first attempt is idempotency-key'd
        // by task ID.
        string tempDir = CreateTempDir();
        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes("cached-reuse-test");
            HttpSourceFetcher sut = MakeFetcher(tempDir, out FakeHandler handler);
            handler.RespondWith(HttpStatusCode.OK, bytes);

            EncodeTask task = MakeTask("/nonexistent/source.mkv", taskId: "t-retry");
            await sut.EnsureLocalAsync(task, CancellationToken.None);

            int requestsAfterFirst = handler.Requests.Count;
            await sut.EnsureLocalAsync(task, CancellationToken.None);

            handler
                .Requests.Count.Should()
                .Be(requestsAfterFirst, "second call must reuse the cached file");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task EnsureLocalAsync_InterruptedDownload_RetryFetchesCompleteSource()
    {
        string tempDir = CreateTempDir();
        try
        {
            HttpSourceFetcher sut = MakeFetcher(tempDir, out FakeHandler handler);
            EncodeTask task = MakeTask("/nonexistent/source.mkv", taskId: "t-interrupted");
            handler.RespondWithStream(new InterruptedStream());

            Func<Task> interrupted = () => sut.EnsureLocalAsync(task, CancellationToken.None);
            await interrupted.Should().ThrowAsync<IOException>();

            string cachedPath = Path.Combine(tempDir, "remote-sources", "t-interrupted.mkv");
            File.Exists(cachedPath).Should().BeFalse("an interrupted source is not reusable");
            Directory
                .GetFiles(Path.GetDirectoryName(cachedPath)!, "*.part")
                .Should()
                .BeEmpty("failed downloads must remove temporary files");

            byte[] completeSource = Encoding.UTF8.GetBytes("complete-source");
            handler.RespondWith(HttpStatusCode.OK, completeSource);
            string local = await sut.EnsureLocalAsync(task, CancellationToken.None);

            local.Should().Be(cachedPath);
            (await File.ReadAllBytesAsync(local)).Should().BeEquivalentTo(completeSource);
            handler.Requests.Should().HaveCount(2);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task EnsureLocalAsync_EmptyCachedFile_ReplacesItWithCompleteSource()
    {
        string tempDir = CreateTempDir();
        try
        {
            string cachedPath = Path.Combine(tempDir, "remote-sources", "t-empty.mkv");
            Directory.CreateDirectory(Path.GetDirectoryName(cachedPath)!);
            await File.WriteAllBytesAsync(cachedPath, []);

            HttpSourceFetcher sut = MakeFetcher(tempDir, out FakeHandler handler);
            byte[] completeSource = Encoding.UTF8.GetBytes("complete-source");
            handler.RespondWith(HttpStatusCode.OK, completeSource);

            string local = await sut.EnsureLocalAsync(
                MakeTask("/nonexistent/source.mkv", taskId: "t-empty"),
                CancellationToken.None
            );

            local.Should().Be(cachedPath);
            (await File.ReadAllBytesAsync(local)).Should().BeEquivalentTo(completeSource);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task EnsureLocalAsync_CoordinatorNonSuccess_Throws()
    {
        string tempDir = CreateTempDir();
        try
        {
            HttpSourceFetcher sut = MakeFetcher(tempDir, out FakeHandler handler);
            handler.RespondWith(HttpStatusCode.NotFound, Encoding.UTF8.GetBytes("not found"));

            EncodeTask task = MakeTask("/nonexistent/source.mkv", taskId: "t-404");

            Func<Task> act = () => sut.EnsureLocalAsync(task, CancellationToken.None);
            await act.Should().ThrowAsync<InvalidOperationException>();
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ReleaseAsync_DeletesCachedFile()
    {
        string tempDir = CreateTempDir();
        try
        {
            HttpSourceFetcher sut = MakeFetcher(tempDir, out FakeHandler handler);
            handler.RespondWith(HttpStatusCode.OK, Encoding.UTF8.GetBytes("x"));

            EncodeTask task = MakeTask("/nonexistent/source.mkv", taskId: "t-release");
            string local = await sut.EnsureLocalAsync(task, CancellationToken.None);
            File.Exists(local).Should().BeTrue();

            await sut.ReleaseAsync(task);

            File.Exists(local).Should().BeFalse("ReleaseAsync must clean up the cached download");
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task NullSourceFetcher_ReturnsInputUnchanged_NoDiskIo()
    {
        NullSourceFetcher sut = new();
        EncodeTask task = MakeTask("/anywhere/file.mkv");

        string local = await sut.EnsureLocalAsync(task, CancellationToken.None);

        local.Should().Be("/anywhere/file.mkv");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────────────

    private static string CreateTempDir()
    {
        string dir = Path.Combine(
            Path.GetTempPath(),
            "nomercy-test-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static EncodeTask MakeTask(string inputPath, string taskId = "task") =>
        new(
            TaskId: taskId,
            Command: new("ffmpeg", ["-i", inputPath, "out.ts"], null),
            OutputPath: "/out/" + taskId,
            Type: EncodeTaskType.QualityVariant,
            InputPath: inputPath
        );

    private static HttpSourceFetcher MakeFetcher(string tempDir, out FakeHandler handler)
    {
        EncoderOptions options = new()
        {
            DistributedEncodingSigningKey = "test-fetch-key-32bytes-length-!!",
            CoordinatorUrl = "http://coordinator.test",
            LiveTranscodeCachePath = tempDir,
        };

        handler = new();
        FakeHandler capturedHandler = handler;

        ServiceCollection services = new();
        services.AddSingleton(options);
        services
            .AddHttpClient()
            .ConfigureHttpClientDefaults(b =>
                b.ConfigurePrimaryHttpMessageHandler(() => capturedHandler)
            );

        ServiceProvider provider = services.BuildServiceProvider();

        return new(
            provider.GetRequiredService<IHttpClientFactory>(),
            options,
            NullLogger<HttpSourceFetcher>.Instance,
            TestStorageFactory.CreateLocal()
        );
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private HttpStatusCode _status = HttpStatusCode.OK;
        private byte[] _body = [];
        private Stream? _stream;
        private readonly List<RequestSnapshot> _requests = [];

        public IReadOnlyList<RequestSnapshot> Requests
        {
            get
            {
                lock (_requests)
                    return _requests.ToArray();
            }
        }

        public void RespondWith(HttpStatusCode status, byte[] body)
        {
            _status = status;
            _body = body;
            _stream = null;
        }

        public void RespondWithStream(Stream stream)
        {
            _status = HttpStatusCode.OK;
            _stream = stream;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_requests)
                _requests.Add(new(request.RequestUri!.Query));

            return Task.FromResult(
                new HttpResponseMessage(_status)
                {
                    Content = _stream is null
                        ? new ByteArrayContent(_body)
                        : new StreamContent(_stream),
                }
            );
        }
    }

    private sealed class InterruptedStream : MemoryStream
    {
        public override async Task CopyToAsync(
            Stream destination,
            int bufferSize,
            CancellationToken cancellationToken
        )
        {
            await destination.WriteAsync("partial"u8.ToArray(), cancellationToken);
            throw new IOException("Coordinator connection dropped");
        }
    }

    public sealed record RequestSnapshot(string Query);
}
