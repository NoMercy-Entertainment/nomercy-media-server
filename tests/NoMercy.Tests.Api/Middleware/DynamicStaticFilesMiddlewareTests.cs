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
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NoMercy.Api.Middleware;
using NoMercy.NmSystem.Monitoring;
using NoMercy.Storage;
using Xunit;

namespace NoMercy.Tests.Api.Middleware;

// DynamicStaticFilesMiddleware is the streaming/range-request layer every
// media file (video segments, subtitles, sprites, fonts) passes through. It
// decides: is this URL even ours (vs. api/swagger/dashboard), is the folder
// registered, does the file exist, should the response be a redirect (S3
// presigned URL), a whole-file copy, or a byte-range slice -- and it must
// translate storage-layer failures into the RIGHT status code (404 vs 502)
// without ever leaking a raw exception to a client mid-stream. None of this
// had a single test before this file.
[Trait("Category", "Middleware")]
public sealed class DynamicStaticFilesMiddlewareTests
{
    private static DefaultHttpContext CreateContext(string path, string? rangeHeader = null)
    {
        DefaultHttpContext context = new();
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        if (rangeHeader is not null)
            context.Request.Headers.Range = rangeHeader;
        return context;
    }

    private static Mock<IStorage> CreateStorage(
        bool exists = true,
        long size = 10,
        byte[]? content = null,
        Uri? presigned = null
    )
    {
        Mock<IStorage> storage = new();
        storage.Setup(s => s.Exists(It.IsAny<string>())).Returns(exists);
        storage.Setup(s => s.Size(It.IsAny<string>())).Returns(size);
        storage
            .Setup(s => s.OpenRead(It.IsAny<string>()))
            .Returns(() => new MemoryStream(content ?? new byte[size]));
        storage
            .Setup(s =>
                s.TryGetPresignedUrlAsync(
                    It.IsAny<string>(),
                    It.IsAny<TimeSpan>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(presigned);
        return storage;
    }

    private static readonly ServedFolderRegistry Registry = new();

    private static (
        DynamicStaticFilesMiddleware Middleware,
        Mock<IStorageFactory> StorageFactory,
        MediaActivityMonitor ActivityMonitor,
        bool[] NextCalled
    ) CreateMiddleware(IStorage? storage = null)
    {
        bool[] nextCalled = [false];
        DynamicStaticFilesMiddleware middleware = new(
            _ =>
            {
                nextCalled[0] = true;
                return Task.CompletedTask;
            },
            Registry,
            NullLogger<DynamicStaticFilesMiddleware>.Instance
        );

        Mock<IStorageFactory> storageFactory = new();
        if (storage is not null)
            storageFactory
                .Setup(f => f.For(It.IsAny<Ulid>(), It.IsAny<Ulid>(), It.IsAny<string>()))
                .Returns(storage);

        return (middleware, storageFactory, new MediaActivityMonitor(), nextCalled);
    }

    private sealed class RegisteredFolder : IDisposable
    {
        public Ulid FolderId { get; }

        public RegisteredFolder(string subPath = "")
        {
            FolderId = Ulid.NewUlid();
            Registry.Add(FolderId, Ulid.NewUlid(), subPath);
        }

        public void Dispose() => Registry.Remove(FolderId);
    }

    [Fact]
    public async Task InvokeAsync_PathHasNoValue_CallsNext()
    {
        (
            DynamicStaticFilesMiddleware middleware,
            Mock<IStorageFactory> storageFactory,
            MediaActivityMonitor activityMonitor,
            bool[] nextCalled
        ) = CreateMiddleware();
        DefaultHttpContext context = new();
        context.Request.Path = default;

        await middleware.InvokeAsync(context, storageFactory.Object, activityMonitor);

        nextCalled[0].Should().BeTrue();
    }

    [Fact]
    public async Task InvokeAsync_PathHasNoSegments_CallsNext()
    {
        (
            DynamicStaticFilesMiddleware middleware,
            Mock<IStorageFactory> storageFactory,
            MediaActivityMonitor activityMonitor,
            bool[] nextCalled
        ) = CreateMiddleware();
        DefaultHttpContext context = CreateContext("/");

        await middleware.InvokeAsync(context, storageFactory.Object, activityMonitor);

        nextCalled[0].Should().BeTrue();
    }

    [Theory]
    [InlineData("/api/movies")]
    [InlineData("/index.html")]
    [InlineData("/swagger-ui/index.html")]
    [InlineData("/images/poster.jpg")]
    [InlineData("/manage/dashboard")]
    public async Task InvokeAsync_SystemRootSegment_CallsNextWithoutTouchingStorage(string path)
    {
        (
            DynamicStaticFilesMiddleware middleware,
            Mock<IStorageFactory> storageFactory,
            MediaActivityMonitor activityMonitor,
            bool[] nextCalled
        ) = CreateMiddleware();
        DefaultHttpContext context = CreateContext(path);

        await middleware.InvokeAsync(context, storageFactory.Object, activityMonitor);

        nextCalled[0].Should().BeTrue();
        storageFactory.Verify(
            f => f.For(It.IsAny<Ulid>(), It.IsAny<Ulid>(), It.IsAny<string>()),
            Times.Never
        );
    }

    [Fact]
    public async Task InvokeAsync_RootSegmentIsNotAUlid_CallsNext()
    {
        (
            DynamicStaticFilesMiddleware middleware,
            Mock<IStorageFactory> storageFactory,
            MediaActivityMonitor activityMonitor,
            bool[] nextCalled
        ) = CreateMiddleware();
        DefaultHttpContext context = CreateContext("/favicon.ico");

        await middleware.InvokeAsync(context, storageFactory.Object, activityMonitor);

        nextCalled[0].Should().BeTrue();
    }

    [Fact]
    public async Task InvokeAsync_FolderNotRegistered_CallsNext()
    {
        (
            DynamicStaticFilesMiddleware middleware,
            Mock<IStorageFactory> storageFactory,
            MediaActivityMonitor activityMonitor,
            bool[] nextCalled
        ) = CreateMiddleware();
        DefaultHttpContext context = CreateContext($"/{Ulid.NewUlid()}/movie.mp4");

        await middleware.InvokeAsync(context, storageFactory.Object, activityMonitor);

        nextCalled[0].Should().BeTrue();
    }

    [Fact]
    public async Task InvokeAsync_StorageFactoryThrows_CallsNextInsteadOfPropagating()
    {
        using RegisteredFolder folder = new();
        (
            DynamicStaticFilesMiddleware middleware,
            Mock<IStorageFactory> storageFactory,
            MediaActivityMonitor activityMonitor,
            bool[] nextCalled
        ) = CreateMiddleware();
        storageFactory
            .Setup(f => f.For(It.IsAny<Ulid>(), It.IsAny<Ulid>(), It.IsAny<string>()))
            .Throws(new InvalidOperationException("driver misconfigured"));
        DefaultHttpContext context = CreateContext($"/{folder.FolderId}/movie.mp4");

        await middleware.InvokeAsync(context, storageFactory.Object, activityMonitor);

        nextCalled[0].Should().BeTrue();
    }

    [Fact]
    public async Task InvokeAsync_StorageExistsThrows_CallsNextInsteadOfPropagating()
    {
        using RegisteredFolder folder = new();
        Mock<IStorage> storage = CreateStorage();
        storage
            .Setup(s => s.Exists(It.IsAny<string>()))
            .Throws(new IOException("nfs mount unavailable"));
        (
            DynamicStaticFilesMiddleware middleware,
            Mock<IStorageFactory> storageFactory,
            MediaActivityMonitor activityMonitor,
            bool[] nextCalled
        ) = CreateMiddleware(storage.Object);
        DefaultHttpContext context = CreateContext($"/{folder.FolderId}/movie.mp4");

        await middleware.InvokeAsync(context, storageFactory.Object, activityMonitor);

        nextCalled[0].Should().BeTrue();
    }

    [Fact]
    public async Task InvokeAsync_FileDoesNotExist_CallsNext()
    {
        using RegisteredFolder folder = new();
        Mock<IStorage> storage = CreateStorage(exists: false);
        (
            DynamicStaticFilesMiddleware middleware,
            Mock<IStorageFactory> storageFactory,
            MediaActivityMonitor activityMonitor,
            bool[] nextCalled
        ) = CreateMiddleware(storage.Object);
        DefaultHttpContext context = CreateContext($"/{folder.FolderId}/missing.mp4");

        await middleware.InvokeAsync(context, storageFactory.Object, activityMonitor);

        nextCalled[0].Should().BeTrue();
    }

    [Fact]
    public async Task InvokeAsync_PresignedUrlAvailable_RedirectsAndSkipsBodyStreaming()
    {
        using RegisteredFolder folder = new();
        Uri presignedUrl = new("https://cdn.example.com/movie.mp4?sig=abc");
        Mock<IStorage> storage = CreateStorage(presigned: presignedUrl);
        (
            DynamicStaticFilesMiddleware middleware,
            Mock<IStorageFactory> storageFactory,
            MediaActivityMonitor activityMonitor,
            bool[] nextCalled
        ) = CreateMiddleware(storage.Object);
        DefaultHttpContext context = CreateContext($"/{folder.FolderId}/movie.mp4");

        await middleware.InvokeAsync(context, storageFactory.Object, activityMonitor);

        context.Response.StatusCode.Should().Be(302);
        context.Response.Headers.Location.ToString().Should().Be(presignedUrl.ToString());
        nextCalled[0].Should().BeFalse();
        activityMonitor.IsActive.Should().BeFalse("a redirect never touches playback activity");
    }

    [Fact]
    public async Task InvokeAsync_NoRangeNonStreamableFile_ServesWholeFileAndTouchesActivity()
    {
        using RegisteredFolder folder = new();
        byte[] content = Encoding.UTF8.GetBytes("WEBVTT\n\n00:00.000 --> 00:01.000\nHi");
        Mock<IStorage> storage = CreateStorage(size: content.Length, content: content);
        (
            DynamicStaticFilesMiddleware middleware,
            Mock<IStorageFactory> storageFactory,
            MediaActivityMonitor activityMonitor,
            _
        ) = CreateMiddleware(storage.Object);
        DefaultHttpContext context = CreateContext($"/{folder.FolderId}/subs.vtt");

        await middleware.InvokeAsync(context, storageFactory.Object, activityMonitor);

        context.Response.StatusCode.Should().Be(200);
        context.Response.ContentLength.Should().Be(content.Length);
        context.Response.ContentType.Should().Be("text/vtt; charset=utf-8");
        activityMonitor.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task InvokeAsync_NoRangeStreamableMedia_ServesWholeFile()
    {
        using RegisteredFolder folder = new();
        byte[] content = new byte[2 * 1024 * 1024];
        Mock<IStorage> storage = CreateStorage(size: content.Length, content: content);
        (
            DynamicStaticFilesMiddleware middleware,
            Mock<IStorageFactory> storageFactory,
            MediaActivityMonitor activityMonitor,
            _
        ) = CreateMiddleware(storage.Object);
        DefaultHttpContext context = CreateContext($"/{folder.FolderId}/movie.mp4");

        await middleware.InvokeAsync(context, storageFactory.Object, activityMonitor);

        // A caller that sent no Range asked for the whole representation. Answering
        // 206 with a probe chunk hands a truncated file to every intermediary that
        // fetches without a Range, and a CDN then replays that chunk for every
        // range the player asks for afterwards.
        context.Response.StatusCode.Should().Be(200);
        context.Response.ContentLength.Should().Be(content.Length);
        context.Response.Headers.AcceptRanges.ToString().Should().Be("bytes");
    }

    [Fact]
    public async Task InvokeAsync_OpenEndedRangeFromZero_StillServesProbeChunk()
    {
        using RegisteredFolder folder = new();
        byte[] content = new byte[2 * 1024 * 1024];
        Mock<IStorage> storage = CreateStorage(size: content.Length, content: content);
        (
            DynamicStaticFilesMiddleware middleware,
            Mock<IStorageFactory> storageFactory,
            MediaActivityMonitor activityMonitor,
            _
        ) = CreateMiddleware(storage.Object);
        DefaultHttpContext context = CreateContext($"/{folder.FolderId}/movie.mp4", "bytes=0-");

        await middleware.InvokeAsync(context, storageFactory.Object, activityMonitor);

        // Browsers open media with `bytes=0-`, so the fast start survives where it
        // was actually meant to apply.
        context.Response.StatusCode.Should().Be(206);
        context.Response.ContentLength.Should().Be(1024 * 1024);
        context.Response.Headers.ContentRange.ToString().Should().StartWith("bytes 0-1048575/");
    }

    [Fact]
    public async Task InvokeAsync_OpenEndedRangeFromOffset_ServesFromThatOffset()
    {
        using RegisteredFolder folder = new();
        byte[] content = new byte[2 * 1024 * 1024];
        Mock<IStorage> storage = CreateStorage(size: content.Length, content: content);
        (
            DynamicStaticFilesMiddleware middleware,
            Mock<IStorageFactory> storageFactory,
            MediaActivityMonitor activityMonitor,
            _
        ) = CreateMiddleware(storage.Object);
        DefaultHttpContext context = CreateContext(
            $"/{folder.FolderId}/movie.mp4",
            "bytes=1048576-"
        );

        await middleware.InvokeAsync(context, storageFactory.Object, activityMonitor);

        context.Response.StatusCode.Should().Be(206);
        context
            .Response.Headers.ContentRange.ToString()
            .Should()
            .StartWith("bytes 1048576-2097151/");
    }

    [Fact]
    public async Task InvokeAsync_RangeWithExplicitEnd_ServesExactSlice()
    {
        using RegisteredFolder folder = new();
        byte[] content = Encoding.UTF8.GetBytes(new string('a', 100));
        Mock<IStorage> storage = CreateStorage(size: content.Length, content: content);
        (
            DynamicStaticFilesMiddleware middleware,
            Mock<IStorageFactory> storageFactory,
            MediaActivityMonitor activityMonitor,
            _
        ) = CreateMiddleware(storage.Object);
        DefaultHttpContext context = CreateContext($"/{folder.FolderId}/movie.mp4", "bytes=10-19");

        await middleware.InvokeAsync(context, storageFactory.Object, activityMonitor);

        context.Response.StatusCode.Should().Be(206);
        context.Response.ContentLength.Should().Be(10);
        context.Response.Headers.ContentRange.ToString().Should().Be("bytes 10-19/100");
        context.Response.Headers.AcceptRanges.ToString().Should().Be("bytes");
        ((MemoryStream)context.Response.Body).ToArray().Should().HaveCount(10);
    }

    [Fact]
    public async Task InvokeAsync_RangeOpenEndedAtZeroOnStreamableMedia_ServesProbeChunk()
    {
        using RegisteredFolder folder = new();
        byte[] content = new byte[2 * 1024 * 1024];
        Mock<IStorage> storage = CreateStorage(size: content.Length, content: content);
        (
            DynamicStaticFilesMiddleware middleware,
            Mock<IStorageFactory> storageFactory,
            MediaActivityMonitor activityMonitor,
            _
        ) = CreateMiddleware(storage.Object);
        DefaultHttpContext context = CreateContext($"/{folder.FolderId}/movie.mp4", "bytes=0-");

        await middleware.InvokeAsync(context, storageFactory.Object, activityMonitor);

        context.Response.StatusCode.Should().Be(206);
        context.Response.ContentLength.Should().Be(1024 * 1024);
        context.Response.Headers.ContentRange.ToString().Should().StartWith("bytes 0-1048575/");
    }

    [Fact]
    public async Task InvokeAsync_ZeroLengthFile_StillServesSuccessfully()
    {
        using RegisteredFolder folder = new();
        Mock<IStorage> storage = CreateStorage(size: 0, content: []);
        (
            DynamicStaticFilesMiddleware middleware,
            Mock<IStorageFactory> storageFactory,
            MediaActivityMonitor activityMonitor,
            _
        ) = CreateMiddleware(storage.Object);
        DefaultHttpContext context = CreateContext($"/{folder.FolderId}/empty.vtt");

        await middleware.InvokeAsync(context, storageFactory.Object, activityMonitor);

        context.Response.StatusCode.Should().Be(200);
        context.Response.ContentLength.Should().Be(0);
    }

    [Fact]
    public async Task InvokeAsync_StreamShorterThanReportedSize_StopsAtActualStreamEnd()
    {
        using RegisteredFolder folder = new();
        byte[] actualContent = [.. "abc"u8];
        Mock<IStorage> storage = new();
        storage.Setup(s => s.Exists(It.IsAny<string>())).Returns(true);
        // Reports a size larger than the stream actually contains -- a real
        // MemoryStream naturally returns 0 once its own backing array is
        // exhausted, which is exactly the "backend handed back fewer bytes
        // than promised" race the read loop's `if (bytesRead == 0) break;`
        // guards against.
        storage.Setup(s => s.Size(It.IsAny<string>())).Returns(100);
        storage
            .Setup(s => s.OpenRead(It.IsAny<string>()))
            .Returns(() => new MemoryStream(actualContent));
        storage
            .Setup(s =>
                s.TryGetPresignedUrlAsync(
                    It.IsAny<string>(),
                    It.IsAny<TimeSpan>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync((Uri?)null);
        (
            DynamicStaticFilesMiddleware middleware,
            Mock<IStorageFactory> storageFactory,
            MediaActivityMonitor activityMonitor,
            _
        ) = CreateMiddleware(storage.Object);
        DefaultHttpContext context = CreateContext($"/{folder.FolderId}/movie.mp4", "bytes=0-99");

        await middleware.InvokeAsync(context, storageFactory.Object, activityMonitor);

        context.Response.StatusCode.Should().Be(206);
        ((MemoryStream)context.Response.Body).ToArray().Should().Equal(actualContent);
    }

    [Fact]
    public async Task InvokeAsync_RangeHeaderWithoutDash_TreatedAsOpenEndedToEof()
    {
        // "bytes=50" has no '-' at all, so Split('-') yields a single-element
        // array -- ranges.Length is 1, short-circuiting the explicit-end check
        // without ever touching ranges[1].
        using RegisteredFolder folder = new();
        byte[] content = new byte[100];
        Mock<IStorage> storage = CreateStorage(size: content.Length, content: content);
        (
            DynamicStaticFilesMiddleware middleware,
            Mock<IStorageFactory> storageFactory,
            MediaActivityMonitor activityMonitor,
            _
        ) = CreateMiddleware(storage.Object);
        DefaultHttpContext context = CreateContext($"/{folder.FolderId}/movie.mp4", "bytes=50");

        await middleware.InvokeAsync(context, storageFactory.Object, activityMonitor);

        context.Response.StatusCode.Should().Be(206);
        context.Response.Headers.ContentRange.ToString().Should().Be("bytes 50-99/100");
    }

    [Fact]
    public async Task InvokeAsync_RangeOpenEndedAtZeroOnNonStreamableFile_ServesToEof()
    {
        // isStreamableMedia is false here, so the "&& start == 0" probe-chunk
        // branch must short-circuit without ever evaluating start -- a
        // non-streamable file under a Range request still serves to EOF.
        using RegisteredFolder folder = new();
        byte[] content = new byte[50];
        Mock<IStorage> storage = CreateStorage(size: content.Length, content: content);
        (
            DynamicStaticFilesMiddleware middleware,
            Mock<IStorageFactory> storageFactory,
            MediaActivityMonitor activityMonitor,
            _
        ) = CreateMiddleware(storage.Object);
        DefaultHttpContext context = CreateContext($"/{folder.FolderId}/subs.vtt", "bytes=0-");

        await middleware.InvokeAsync(context, storageFactory.Object, activityMonitor);

        context.Response.StatusCode.Should().Be(206);
        context.Response.Headers.ContentRange.ToString().Should().Be("bytes 0-49/50");
    }

    [Fact]
    public async Task InvokeAsync_RangeOpenEndedNonZeroStart_ServesToEndOfFile()
    {
        using RegisteredFolder folder = new();
        byte[] content = new byte[100];
        Mock<IStorage> storage = CreateStorage(size: content.Length, content: content);
        (
            DynamicStaticFilesMiddleware middleware,
            Mock<IStorageFactory> storageFactory,
            MediaActivityMonitor activityMonitor,
            _
        ) = CreateMiddleware(storage.Object);
        DefaultHttpContext context = CreateContext($"/{folder.FolderId}/movie.mp4", "bytes=50-");

        await middleware.InvokeAsync(context, storageFactory.Object, activityMonitor);

        context.Response.StatusCode.Should().Be(206);
        context.Response.Headers.ContentRange.ToString().Should().Be("bytes 50-99/100");
    }

    [Fact]
    public async Task InvokeAsync_ExplicitEndPastEndOfFile_IsClampedToLastByte()
    {
        using RegisteredFolder folder = new();
        byte[] content = new byte[100];
        Mock<IStorage> storage = CreateStorage(size: content.Length, content: content);
        (
            DynamicStaticFilesMiddleware middleware,
            Mock<IStorageFactory> storageFactory,
            MediaActivityMonitor activityMonitor,
            _
        ) = CreateMiddleware(storage.Object);
        DefaultHttpContext context = CreateContext(
            $"/{folder.FolderId}/movie.mp4",
            "bytes=90-999999"
        );

        await middleware.InvokeAsync(context, storageFactory.Object, activityMonitor);

        context.Response.StatusCode.Should().Be(206);
        context.Response.Headers.ContentRange.ToString().Should().Be("bytes 90-99/100");
    }

    [Fact]
    public async Task InvokeAsync_RangeWithNonNumericStart_Returns416()
    {
        using RegisteredFolder folder = new();
        Mock<IStorage> storage = CreateStorage(size: 100);
        (
            DynamicStaticFilesMiddleware middleware,
            Mock<IStorageFactory> storageFactory,
            MediaActivityMonitor activityMonitor,
            _
        ) = CreateMiddleware(storage.Object);
        DefaultHttpContext context = CreateContext($"/{folder.FolderId}/movie.mp4", "bytes=abc-99");

        await middleware.InvokeAsync(context, storageFactory.Object, activityMonitor);

        context.Response.StatusCode.Should().Be((int)HttpStatusCode.RequestedRangeNotSatisfiable);
    }

    [Fact]
    public async Task InvokeAsync_RangeWithNonNumericEnd_Returns416()
    {
        using RegisteredFolder folder = new();
        Mock<IStorage> storage = CreateStorage(size: 100);
        (
            DynamicStaticFilesMiddleware middleware,
            Mock<IStorageFactory> storageFactory,
            MediaActivityMonitor activityMonitor,
            _
        ) = CreateMiddleware(storage.Object);
        DefaultHttpContext context = CreateContext($"/{folder.FolderId}/movie.mp4", "bytes=0-xyz");

        await middleware.InvokeAsync(context, storageFactory.Object, activityMonitor);

        context.Response.StatusCode.Should().Be((int)HttpStatusCode.RequestedRangeNotSatisfiable);
    }

    [Fact]
    public async Task InvokeAsync_RangeStartBeyondFileEnd_Returns416()
    {
        using RegisteredFolder folder = new();
        Mock<IStorage> storage = CreateStorage(size: 10);
        (
            DynamicStaticFilesMiddleware middleware,
            Mock<IStorageFactory> storageFactory,
            MediaActivityMonitor activityMonitor,
            _
        ) = CreateMiddleware(storage.Object);
        // start(500) will be clamped-compared against end (fileLength-1=9) -> start > end.
        DefaultHttpContext context = CreateContext($"/{folder.FolderId}/movie.mp4", "bytes=500-");

        await middleware.InvokeAsync(context, storageFactory.Object, activityMonitor);

        context.Response.StatusCode.Should().Be((int)HttpStatusCode.RequestedRangeNotSatisfiable);
    }

    [Theory]
    [InlineData("captions.vtt", "text/vtt; charset=utf-8")]
    [InlineData("playlist.m3u8", "application/vnd.apple.mpegurl")]
    [InlineData("segment.ts", "video/mp2t")]
    [InlineData("font.woff2", "font/woff2")]
    [InlineData("subtitle.srt", "application/x-subrip; charset=utf-8")]
    public async Task InvokeAsync_KnownExtension_UsesContentTypeOverride(
        string filename,
        string expectedContentType
    )
    {
        using RegisteredFolder folder = new();
        byte[] content = [.. "content"u8];
        Mock<IStorage> storage = CreateStorage(size: content.Length, content: content);
        (
            DynamicStaticFilesMiddleware middleware,
            Mock<IStorageFactory> storageFactory,
            MediaActivityMonitor activityMonitor,
            _
        ) = CreateMiddleware(storage.Object);
        DefaultHttpContext context = CreateContext($"/{folder.FolderId}/{filename}");

        await middleware.InvokeAsync(context, storageFactory.Object, activityMonitor);

        context.Response.ContentType.Should().Be(expectedContentType);
    }

    [Fact]
    public async Task InvokeAsync_UnmappedExtension_FallsBackToGenericMimeMapping()
    {
        using RegisteredFolder folder = new();
        byte[] content = [.. "content"u8];
        Mock<IStorage> storage = CreateStorage(size: content.Length, content: content);
        (
            DynamicStaticFilesMiddleware middleware,
            Mock<IStorageFactory> storageFactory,
            MediaActivityMonitor activityMonitor,
            _
        ) = CreateMiddleware(storage.Object);
        DefaultHttpContext context = CreateContext($"/{folder.FolderId}/poster.jpg");

        await middleware.InvokeAsync(context, storageFactory.Object, activityMonitor);

        context.Response.ContentType.Should().Be("image/jpeg");
    }

    [Fact]
    public async Task InvokeAsync_FileVanishesMidServe_Returns404()
    {
        using RegisteredFolder folder = new();
        Mock<IStorage> storage = CreateStorage();
        storage.Setup(s => s.Size(It.IsAny<string>())).Throws(new FileNotFoundException("gone"));
        (
            DynamicStaticFilesMiddleware middleware,
            Mock<IStorageFactory> storageFactory,
            MediaActivityMonitor activityMonitor,
            _
        ) = CreateMiddleware(storage.Object);
        DefaultHttpContext context = CreateContext($"/{folder.FolderId}/movie.mp4");

        await middleware.InvokeAsync(context, storageFactory.Object, activityMonitor);

        context.Response.StatusCode.Should().Be((int)HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task InvokeAsync_StorageTransportFailureDuringServe_Returns502()
    {
        using RegisteredFolder folder = new();
        Mock<IStorage> storage = CreateStorage();
        storage.Setup(s => s.Size(It.IsAny<string>())).Throws(new IOException("nfs hiccup"));
        (
            DynamicStaticFilesMiddleware middleware,
            Mock<IStorageFactory> storageFactory,
            MediaActivityMonitor activityMonitor,
            _
        ) = CreateMiddleware(storage.Object);
        DefaultHttpContext context = CreateContext($"/{folder.FolderId}/movie.mp4");

        await middleware.InvokeAsync(context, storageFactory.Object, activityMonitor);

        context.Response.StatusCode.Should().Be((int)HttpStatusCode.BadGateway);
    }

    [Fact]
    public async Task InvokeAsync_ClientDisconnectMidStream_LeavesResponseUntouched()
    {
        using RegisteredFolder folder = new();
        Mock<IStorage> storage = CreateStorage();
        storage
            .Setup(s => s.Size(It.IsAny<string>()))
            .Throws(new OperationCanceledException("client gone"));
        (
            DynamicStaticFilesMiddleware middleware,
            Mock<IStorageFactory> storageFactory,
            MediaActivityMonitor activityMonitor,
            _
        ) = CreateMiddleware(storage.Object);
        DefaultHttpContext context = CreateContext($"/{folder.FolderId}/movie.mp4");
        CancellationTokenSource cts = new();
        cts.Cancel();
        context.RequestAborted = cts.Token;

        await middleware.InvokeAsync(context, storageFactory.Object, activityMonitor);

        // No status was ever set for an aborted client — the default success
        // code stands because there is no client left to send anything to.
        context.Response.StatusCode.Should().Be(200);
    }

    [Fact]
    public async Task InvokeAsync_OperationCanceledButRequestNotAborted_FallsThroughToGenericHandler()
    {
        // The catch on OperationCanceledException is guarded by
        // `when (context.RequestAborted.IsCancellationRequested)`. A cancellation
        // that is NOT tied to client disconnect (e.g. an internal timeout) must
        // fail that guard and fall through to the generic catch-all as a 502,
        // not be silently swallowed as if the client had gone away.
        using RegisteredFolder folder = new();
        Mock<IStorage> storage = CreateStorage();
        storage
            .Setup(s => s.Size(It.IsAny<string>()))
            .Throws(new OperationCanceledException("unrelated timeout"));
        (
            DynamicStaticFilesMiddleware middleware,
            Mock<IStorageFactory> storageFactory,
            MediaActivityMonitor activityMonitor,
            _
        ) = CreateMiddleware(storage.Object);
        DefaultHttpContext context = CreateContext($"/{folder.FolderId}/movie.mp4");
        context.RequestAborted = CancellationToken.None;

        await middleware.InvokeAsync(context, storageFactory.Object, activityMonitor);

        context.Response.StatusCode.Should().Be((int)HttpStatusCode.BadGateway);
    }

    [Fact]
    public async Task InvokeAsync_UnhandledException_Returns502()
    {
        using RegisteredFolder folder = new();
        Mock<IStorage> storage = CreateStorage();
        storage
            .Setup(s => s.Size(It.IsAny<string>()))
            .Throws(new InvalidOperationException("boom"));
        (
            DynamicStaticFilesMiddleware middleware,
            Mock<IStorageFactory> storageFactory,
            MediaActivityMonitor activityMonitor,
            _
        ) = CreateMiddleware(storage.Object);
        DefaultHttpContext context = CreateContext($"/{folder.FolderId}/movie.mp4");

        await middleware.InvokeAsync(context, storageFactory.Object, activityMonitor);

        context.Response.StatusCode.Should().Be((int)HttpStatusCode.BadGateway);
    }

    [Fact]
    public void AddFolder_ThenRemoveFolder_ForgetsTheRegistration()
    {
        Ulid folderId = Ulid.NewUlid();
        Registry.Add(folderId, Ulid.NewUlid(), "sub/path");

        Registry.Remove(folderId);

        // Re-removing an already-forgotten folder must be a safe no-op (used by
        // the RegisteredFolder test fixture's Dispose across every test above).
        Registry.Remove(folderId);
    }

    [Fact]
    public async Task InvokeAsync_FolderRegisteredWithNullSubPath_TreatsItAsRootScope()
    {
        // subPath is declared non-nullable, but AddFolder still defends against
        // a caller that ignores the nullable contract (e.g. a driver row whose
        // SubPath column is legitimately NULL in the database). Passing a
        // null! here pins that the defensive `?? string.Empty` actually runs
        // rather than just being dead decoration.
        Ulid folderId = Ulid.NewUlid();
        Registry.Add(folderId, Ulid.NewUlid(), null!);
        try
        {
            Mock<IStorage> storage = CreateStorage();
            (
                DynamicStaticFilesMiddleware middleware,
                Mock<IStorageFactory> storageFactory,
                MediaActivityMonitor activityMonitor,
                _
            ) = CreateMiddleware(storage.Object);
            DefaultHttpContext context = CreateContext($"/{folderId}/movie.mp4");

            await middleware.InvokeAsync(context, storageFactory.Object, activityMonitor);

            storageFactory.Verify(f => f.For(folderId, It.IsAny<Ulid>(), string.Empty), Times.Once);
        }
        finally
        {
            Registry.Remove(folderId);
        }
    }

    [Theory]
    [InlineData("bytes=0-", true, 0, 1024 * 1024 - 1)]
    [InlineData("bytes=0-", false, 0, 9_999_999)]
    [InlineData("bytes=5000-", true, 5000, 9_999_999)]
    [InlineData("bytes=100-199", true, 100, 199)]
    [InlineData("bytes=100-99999999", true, 100, 9_999_999)]
    public void TryResolveRange_SatisfiableRange_ResolvesStartAndEnd(
        string header,
        bool streamable,
        long expectedStart,
        long expectedEnd
    )
    {
        bool resolved = DynamicStaticFilesMiddleware.TryResolveRange(
            header,
            10_000_000,
            streamable,
            out long start,
            out long end
        );

        resolved.Should().BeTrue();
        start.Should().Be(expectedStart);
        end.Should().Be(expectedEnd);
    }

    [Theory]
    [InlineData("bytes=abc-", 100)]
    [InlineData("bytes=0-xyz", 100)]
    [InlineData("bytes=100-", 100)]
    [InlineData("bytes=0-", 0)]
    public void TryResolveRange_UnsatisfiableRange_IsRejected(string header, long fileLength)
    {
        DynamicStaticFilesMiddleware
            .TryResolveRange(header, fileLength, true, out _, out _)
            .Should()
            .BeFalse();
    }
}
