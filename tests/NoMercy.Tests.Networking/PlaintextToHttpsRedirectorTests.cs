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

using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http.Features;
using NoMercy.Networking.Certificate;
using NoMercy.NmSystem.Networking;
using Xunit;

namespace NoMercy.Tests.Networking;

[Trait("Category", "Unit")]
public sealed class PlaintextToHttpsRedirectorTests
{
    private static ReadOnlySequence<byte> Request(string raw) => new(Encoding.ASCII.GetBytes(raw));

    [Fact]
    public void ParseHostHeader_ReturnsHost_WithoutPlaintextPort()
    {
        ReadOnlySequence<byte> buffer = Request(
            "GET / HTTP/1.1\r\nHost: 10.0.1.1:7626\r\nConnection: keep-alive\r\n\r\n"
        );

        string? host = PlaintextToHttpsRedirector.ParseHostHeader(buffer);

        Assert.Equal("10.0.1.1", host);
    }

    [Fact]
    public void ParseHostHeader_ReturnsSynthesizedDnsHost_Unmodified()
    {
        ReadOnlySequence<byte> buffer = Request(
            "GET /dashboard HTTP/1.1\r\n"
                + "Host: 10-0-1-1.00380881-eb96-44e9-8e59-1258568d1a1f.srv.nomercy.tv:7626\r\n"
                + "\r\n"
        );

        string? host = PlaintextToHttpsRedirector.ParseHostHeader(buffer);

        Assert.Equal("10-0-1-1.00380881-eb96-44e9-8e59-1258568d1a1f.srv.nomercy.tv", host);
    }

    [Fact]
    public void ParseHostHeader_ReturnsNull_WhenHeaderMissing()
    {
        ReadOnlySequence<byte> buffer = Request("GET / HTTP/1.1\r\nConnection: keep-alive\r\n\r\n");

        string? host = PlaintextToHttpsRedirector.ParseHostHeader(buffer);

        Assert.Null(host);
    }

    [Fact]
    public void ParseRequestPath_ReturnsPathWithQueryString()
    {
        ReadOnlySequence<byte> buffer = Request(
            "GET /api/v1/search?q=matrix HTTP/1.1\r\nHost: 10.0.1.1:7626\r\n\r\n"
        );

        string path = PlaintextToHttpsRedirector.ParseRequestPath(buffer);

        Assert.Equal("/api/v1/search?q=matrix", path);
    }

    [Fact]
    public void ParseRequestPath_FallsBackToRoot_WhenRequestLineIsMalformed()
    {
        ReadOnlySequence<byte> buffer = Request("not an http request at all");

        string path = PlaintextToHttpsRedirector.ParseRequestPath(buffer);

        Assert.Equal("/", path);
    }

    /// <summary>
    /// The sniff must hand the TLS adapter a pipe it can read straight away. Marking
    /// the ClientHello as examined parks that read until bytes arrive past it, and the
    /// client sends none — it is waiting for the ServerHello. Every https request to
    /// the internal port hung on exactly that, while plain http still answered 301.
    /// </summary>
    [Fact]
    public async Task ATlsHandshake_IsStillReadableByTheNextHandler()
    {
        byte[] clientHello = [0x16, 0x03, 0x01, 0x00, 0x2a];
        FakeConnection connection = new();
        await connection.ClientWritesAsync(clientHello);

        ReadResult seenByNext = default;
        Task handled = PlaintextToHttpsRedirector.HandleAsync(
            connection,
            async _ =>
                seenByNext = await connection
                    .Transport.Input.ReadAsync(TestTimeout.Token)
                    .AsTask()
                    .WaitAsync(TestTimeout.Token),
            7626
        );

        await handled.WaitAsync(TestTimeout.Token);

        Assert.Equal(clientHello, seenByNext.Buffer.ToArray());
    }

    [Fact]
    public async Task APlaintextRequest_IsAnsweredWithARedirectToTheHttpsPort()
    {
        FakeConnection connection = new();
        await connection.ClientWritesAsync(
            Encoding.ASCII.GetBytes("GET /dashboard HTTP/1.1\r\nHost: 10.0.1.1:7626\r\n\r\n")
        );

        bool reachedNext = false;
        await PlaintextToHttpsRedirector
            .HandleAsync(
                connection,
                _ =>
                {
                    reachedNext = true;
                    return Task.CompletedTask;
                },
                7626
            )
            .WaitAsync(TestTimeout.Token);

        string response = await connection.ClientReadsAsync(TestTimeout.Token);

        Assert.False(reachedNext, "a plaintext request never belongs in the TLS adapter");
        Assert.Contains("301 Moved Permanently", response);
        Assert.Contains("Location: https://10.0.1.1:7626/dashboard", response);
    }

    /// <summary>
    /// The API checks reachability over plain http, because it must work before a
    /// certificate exists. Once the port serves TLS, the same plain http call lands here,
    /// and a redirect would read as "unreachable".
    /// </summary>
    [Fact]
    public async Task APlaintextReachabilityCall_WithALiveCode_IsAnsweredWithTheCode()
    {
        string code = ReachCodes.Shared.Issue();
        try
        {
            FakeConnection connection = new();
            await connection.ClientWritesAsync(
                Encoding.ASCII.GetBytes(
                    $"GET {ReachCodes.PathPrefix}{code} HTTP/1.1\r\nHost: 192.0.2.10:7626\r\n\r\n"
                )
            );

            await PlaintextToHttpsRedirector
                .HandleAsync(connection, _ => Task.CompletedTask, 7626)
                .WaitAsync(TestTimeout.Token);

            string response = await connection.ClientReadsAsync(TestTimeout.Token);

            Assert.StartsWith("HTTP/1.1 200 OK", response);
            Assert.EndsWith("\r\n\r\n" + code, response);
        }
        finally
        {
            ReachCodes.Shared.Revoke(code);
        }
    }

    [Fact]
    public async Task APlaintextReachabilityCall_WithAnUnknownCode_IsRedirectedLikeAnyOtherPath()
    {
        FakeConnection connection = new();
        await connection.ClientWritesAsync(
            Encoding.ASCII.GetBytes(
                $"GET {ReachCodes.PathPrefix}unknown HTTP/1.1\r\nHost: 192.0.2.10:7626\r\n\r\n"
            )
        );

        await PlaintextToHttpsRedirector
            .HandleAsync(connection, _ => Task.CompletedTask, 7626)
            .WaitAsync(TestTimeout.Token);

        string response = await connection.ClientReadsAsync(TestTimeout.Token);

        Assert.Contains("301 Moved Permanently", response);
    }

    /// <summary>
    /// A peer that hangs up before sending its first byte is ordinary traffic — port
    /// scans, health probes and speculative browser connections all do it — and the
    /// read is cancelled by ConnectionClosed. That escaped as an unhandled exception
    /// and Kestrel logged a stack trace per connection, burying real errors under it.
    /// </summary>
    [Fact]
    public async Task APeerThatHangsUpBeforeSendingAnything_IsNotAnError()
    {
        FakeConnection connection = new();
        CancellationTokenSource closed = new();
        connection.ConnectionClosed = closed.Token;
        await closed.CancelAsync();

        Exception? thrown = await Record.ExceptionAsync(() =>
            PlaintextToHttpsRedirector
                .HandleAsync(connection, _ => Task.CompletedTask, 7626)
                .WaitAsync(TestTimeout.Token)
        );

        Assert.Null(thrown);
    }

    /// <summary>
    /// The catch above is narrow on purpose: only a cancellation the CONNECTION caused
    /// is swallowed. A cancellation from anywhere else is a real fault and must still
    /// surface, or this fix trades a noisy log for a silent one.
    /// </summary>
    [Fact]
    public async Task ACancellationTheConnectionDidNotCause_StillSurfaces()
    {
        byte[] clientHello = [0x16, 0x03, 0x01, 0x00, 0x2a];
        FakeConnection connection = new();
        await connection.ClientWritesAsync(clientHello);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            PlaintextToHttpsRedirector
                .HandleAsync(connection, _ => throw new OperationCanceledException(), 7626)
                .WaitAsync(TestTimeout.Token)
        );
    }

    private static CancellationTokenSource TestTimeout => new(TimeSpan.FromSeconds(5));

    /// <summary>
    /// A connection whose transport is a real pair of pipes, so the redirector's pipe
    /// bookkeeping is exercised rather than described.
    /// </summary>
    private sealed class FakeConnection : ConnectionContext, IDuplexPipe
    {
        private readonly Pipe _clientToServer = new();
        private readonly Pipe _serverToClient = new();

        public PipeReader Input => _clientToServer.Reader;
        public PipeWriter Output => _serverToClient.Writer;

        public override IDuplexPipe Transport { get; set; }
        public override string ConnectionId { get; set; } = "test";
        public override CancellationToken ConnectionClosed { get; set; }
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override IDictionary<object, object?> Items { get; set; } =
            new Dictionary<object, object?>();

        public FakeConnection()
        {
            Transport = this;
        }

        public async Task ClientWritesAsync(byte[] bytes)
        {
            await _clientToServer.Writer.WriteAsync(bytes);
        }

        // Bounded, because the failure this guards against is a read that never
        // returns: a hanging test reports nothing, where a failing one names the bug.
        public async Task<string> ClientReadsAsync(CancellationToken ct)
        {
            ReadResult read = await _serverToClient.Reader.ReadAsync(ct).AsTask().WaitAsync(ct);
            return Encoding.ASCII.GetString(read.Buffer.ToArray());
        }
    }
}
