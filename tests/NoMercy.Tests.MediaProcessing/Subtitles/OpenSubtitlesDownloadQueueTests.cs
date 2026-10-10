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
using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.MediaProcessing.Subtitles;

namespace NoMercy.Tests.MediaProcessing.Subtitles;

/// <summary>
/// Downloads hit a different host to the searches and so never went through the client's rate
/// limit queue: a backlog sweep issued them as fast as its loop ran. These pin that they are
/// paced, and that a request someone is waiting on is not stuck behind the sweep.
/// </summary>
public class OpenSubtitlesDownloadQueueTests
{
    private static byte[] Gzip(string content)
    {
        using MemoryStream destination = new();
        using (GZipStream gzip = new(destination, CompressionMode.Compress))
        {
            byte[] raw = Encoding.UTF8.GetBytes(content);
            gzip.Write(raw, 0, raw.Length);
        }

        return destination.ToArray();
    }

    [Fact]
    public async Task DownloadSubtitleAsync_SerialisesConcurrentDownloads()
    {
        RecordingHandler handler = new(Gzip("1\n00:00:01,000 --> 00:00:02,000\ncue"));
        OpenSubtitlesProvider provider = new(
            NullLogger<OpenSubtitlesProvider>.Instance,
            new StubFactory(handler)
        );

        await Task.WhenAll(
            Enumerable
                .Range(0, 5)
                .Select(i =>
                    provider.DownloadSubtitleAsync(
                        $"https://dl.opensubtitles.org/{i}",
                        CancellationToken.None
                    )
                )
        );

        handler.Requests.Should().HaveCount(5);
        handler.MaxConcurrent.Should().Be(1, "the queue admits one download at a time");
    }

    [Fact]
    public async Task DownloadSubtitleAsync_StillReturnsCueBytesThroughTheQueue()
    {
        const string srt = "1\n00:00:01,000 --> 00:00:04,000\nWat als...?";
        RecordingHandler handler = new(Gzip(srt));
        OpenSubtitlesProvider provider = new(
            NullLogger<OpenSubtitlesProvider>.Instance,
            new StubFactory(handler)
        );

        byte[] result = await provider.DownloadSubtitleAsync(
            "https://dl.opensubtitles.org/x",
            CancellationToken.None,
            priority: true
        );

        Encoding.UTF8.GetString(result).Should().Be(srt);
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class RecordingHandler(byte[] body) : HttpMessageHandler
    {
        private int _concurrent;

        public ConcurrentBag<string> Requests { get; } = [];
        public int MaxConcurrent { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            int running = Interlocked.Increment(ref _concurrent);
            lock (Requests)
                MaxConcurrent = Math.Max(MaxConcurrent, running);

            Requests.Add(request.RequestUri?.ToString() ?? string.Empty);

            try
            {
                await Task.Delay(40, cancellationToken);
                return new(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
            }
            finally
            {
                Interlocked.Decrement(ref _concurrent);
            }
        }
    }
}
