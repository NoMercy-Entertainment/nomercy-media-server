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
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.Encoder.Composition;
using NoMercy.Encoder.Notifications;

namespace NoMercy.Tests.Encoder.Notifications;

public class WebhookNotificationDispatcherTests
{
    [Fact]
    public async Task Notify_NoUrlsConfigured_DoesNotSendAnyRequest()
    {
        EncoderOptions options = new() { FfmpegPathOverride = "ffmpeg" };
        CapturingHandler handler = new() { StatusCode = HttpStatusCode.OK };
        WebhookNotificationDispatcher dispatcher = BuildDispatcher(options, handler);

        await dispatcher.NotifyStartedAsync(new(1, "/in", "/out", "HLS"), CancellationToken.None);

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Notify_SingleUrl_Success_PostsOnce()
    {
        EncoderOptions options = BuildOptions("https://example.com/hook");
        CapturingHandler handler = new() { StatusCode = HttpStatusCode.OK };
        WebhookNotificationDispatcher dispatcher = BuildDispatcher(options, handler);

        await dispatcher.NotifyStartedAsync(
            new(42, "/in.mkv", "/out", "HLS 1080p"),
            CancellationToken.None
        );

        Assert.Single(handler.Requests);
        Assert.Equal("https://example.com/hook", handler.Requests[0].url);
        Assert.Contains("encoding.started", handler.Requests[0].body);
        Assert.Contains("\"job_id\":42", handler.Requests[0].body);
    }

    [Fact]
    public async Task Notify_MultipleUrls_PostsToEach()
    {
        EncoderOptions options = BuildOptions(["https://a.example/hook", "https://b.example/hook"]);
        CapturingHandler handler = new() { StatusCode = HttpStatusCode.OK };
        WebhookNotificationDispatcher dispatcher = BuildDispatcher(options, handler);

        await dispatcher.NotifyCompletedAsync(
            new(7, "/out.mp4", TimeSpan.FromSeconds(120)),
            CancellationToken.None
        );

        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains(handler.Requests, r => r.url == "https://a.example/hook");
        Assert.Contains(handler.Requests, r => r.url == "https://b.example/hook");
    }

    [Fact]
    public async Task Notify_Completed_PayloadShapeMatchesSpec()
    {
        EncoderOptions options = BuildOptions("https://example.com/hook");
        CapturingHandler handler = new() { StatusCode = HttpStatusCode.OK };
        WebhookNotificationDispatcher dispatcher = BuildDispatcher(options, handler);

        await dispatcher.NotifyCompletedAsync(
            new(99, "/out.m3u8", TimeSpan.FromSeconds(180)),
            CancellationToken.None
        );

        JsonDocument doc = JsonDocument.Parse(handler.Requests[0].body);
        Assert.Equal("encoding.completed", doc.RootElement.GetProperty("event").GetString());
        Assert.True(doc.RootElement.TryGetProperty("timestamp", out _));
        JsonElement payload = doc.RootElement.GetProperty("payload");
        Assert.Equal(99, payload.GetProperty("job_id").GetInt32());
        Assert.Equal("/out.m3u8", payload.GetProperty("output_path").GetString());
        Assert.Equal(180.0, payload.GetProperty("duration_seconds").GetDouble());
    }

    [Fact]
    public async Task Notify_Failed_PayloadIncludesErrorAndExceptionType()
    {
        EncoderOptions options = BuildOptions("https://example.com/hook");
        CapturingHandler handler = new() { StatusCode = HttpStatusCode.OK };
        WebhookNotificationDispatcher dispatcher = BuildDispatcher(options, handler);

        await dispatcher.NotifyFailedAsync(
            new(5, "/src.mkv", "ffmpeg crashed", "ProcessCrashedException"),
            CancellationToken.None
        );

        JsonDocument doc = JsonDocument.Parse(handler.Requests[0].body);
        Assert.Equal("encoding.failed", doc.RootElement.GetProperty("event").GetString());
        JsonElement payload = doc.RootElement.GetProperty("payload");
        Assert.Equal("ffmpeg crashed", payload.GetProperty("error_message").GetString());
        Assert.Equal("ProcessCrashedException", payload.GetProperty("exception_type").GetString());
    }

    [Fact]
    public async Task Notify_TransientFailure_RetriesUpToThreeTimes()
    {
        EncoderOptions options = BuildOptions("https://example.com/hook");
        CapturingHandler handler = new() { StatusCode = HttpStatusCode.InternalServerError };
        WebhookNotificationDispatcher dispatcher = BuildDispatcher(options, handler);

        using CancellationTokenSource cts = new();
        // Give retries enough time but cancel before the third backoff (4s) completes
        // so the test runs fast — we still see 3 request attempts.
        cts.CancelAfter(TimeSpan.FromSeconds(4));

        await dispatcher.NotifyStartedAsync(new(1, "/in", "/out", "HLS"), cts.Token);

        Assert.InRange(handler.Requests.Count, 2, 3);
    }

    [Fact]
    public async Task Notify_OneUrlFails_OtherStillGetsNotified()
    {
        EncoderOptions options = BuildOptions([
            "https://bad.example/hook",
            "https://good.example/hook",
        ]);
        PerUrlHandler handler = new(
            new Dictionary<string, HttpStatusCode>
            {
                ["https://bad.example/hook"] = HttpStatusCode.InternalServerError,
                ["https://good.example/hook"] = HttpStatusCode.OK,
            }
        );
        WebhookNotificationDispatcher dispatcher = BuildDispatcher(options, handler);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(6));
        await dispatcher.NotifyStartedAsync(new(1, "/in", "/out", "HLS"), cts.Token);

        // Good URL gets at least one request even though bad URL is failing.
        Assert.Contains(
            handler.Requests,
            r => r is { url: "https://good.example/hook", statusCode: HttpStatusCode.OK }
        );
    }

    [Fact]
    public async Task Notify_OperationCancelled_StopsImmediately()
    {
        EncoderOptions options = BuildOptions("https://example.com/hook");
        CapturingHandler handler = new() { StatusCode = HttpStatusCode.OK };
        WebhookNotificationDispatcher dispatcher = BuildDispatcher(options, handler);

        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        await dispatcher.NotifyStartedAsync(new(1, "/in", "/out", "HLS"), cts.Token);

        // First attempt short-circuits before the request starts.
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Notify_FailingWebhook_LogsHostOnlyNeverThePath()
    {
        const string secretPath = "/hooks/placeholder-token-0000";
        EncoderOptions options = BuildOptions($"https://example.test{secretPath}");
        CapturingHandler handler = new() { StatusCode = HttpStatusCode.InternalServerError };
        CapturingLogger logger = new();
        WebhookNotificationDispatcher dispatcher = new(options, new(handler), logger);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(2));
        await dispatcher.NotifyStartedAsync(new(1, "/in", "/out", "HLS"), cts.Token);

        Assert.NotEmpty(logger.Messages);
        Assert.All(logger.Messages, message => Assert.DoesNotContain(secretPath, message));
        Assert.Contains(logger.Messages, message => message.Contains("example.test"));
    }

    private sealed class CapturingLogger : ILogger<WebhookNotificationDispatcher>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => Messages.Add(formatter(state, exception));
    }

    private static EncoderOptions BuildOptions(params string[] urls)
    {
        EncoderOptions options = new() { FfmpegPathOverride = "ffmpeg" };
        foreach (string url in urls)
            options.NotificationWebhookUrls.Add(url);
        return options;
    }

    private static WebhookNotificationDispatcher BuildDispatcher(
        EncoderOptions options,
        HttpMessageHandler handler
    )
    {
        HttpClient client = new(handler);
        return new(options, client, NullLogger<WebhookNotificationDispatcher>.Instance);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;
        public List<(string url, string body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            string body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.RequestUri!.ToString(), body));
            return new(StatusCode);
        }
    }

    private sealed class PerUrlHandler(IDictionary<string, HttpStatusCode> urlToStatus)
        : HttpMessageHandler
    {
        public List<(string url, HttpStatusCode statusCode)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            string url = request.RequestUri!.ToString();
            HttpStatusCode status = urlToStatus.TryGetValue(url, out HttpStatusCode s)
                ? s
                : HttpStatusCode.OK;
            Requests.Add((url, status));
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }
}
