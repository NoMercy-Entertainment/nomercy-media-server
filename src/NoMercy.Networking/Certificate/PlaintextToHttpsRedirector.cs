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
using System.Text;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using NoMercy.NmSystem.Networking;

namespace NoMercy.Networking.Certificate;

/// <summary>
/// Once the internal port is serving TLS, a plaintext HTTP request against it never
/// reaches the HTTP pipeline — Kestrel's TLS adapter reads the first bytes expecting a
/// TLS ClientHello (0x16), finds an HTTP request line instead, and drops the connection.
/// The browser reports that as an empty response, not a redirect, so a stale
/// "http://" bookmark or a typed URL looks like the server is dead. This middleware sits
/// in front of the TLS adapter, peeks the first byte without consuming it, and — only for
/// non-TLS traffic — hand-writes a 301 to the same host/path over https instead of letting
/// the TLS handshake fail silently.
/// </summary>
internal static class PlaintextToHttpsRedirector
{
    private const byte TlsHandshakeContentType = 0x16;

    public static void Install(ListenOptions listenOptions, int httpsPort)
    {
        listenOptions.Use(next =>
            connectionContext => HandleAsync(connectionContext, next, httpsPort)
        );
    }

    internal static async Task HandleAsync(
        ConnectionContext connectionContext,
        ConnectionDelegate next,
        int httpsPort
    )
    {
        try
        {
            await PeekAndRedirectAsync(connectionContext, next, httpsPort);
        }
        catch (OperationCanceledException)
            when (connectionContext.ConnectionClosed.IsCancellationRequested)
        {
            // The peer hung up before we read its first byte, which every port scan,
            // health probe and speculative browser connection does. There is no
            // connection left to answer, so ending quietly IS the handling.
        }
    }

    private static async Task PeekAndRedirectAsync(
        ConnectionContext connectionContext,
        ConnectionDelegate next,
        int httpsPort
    )
    {
        System.IO.Pipelines.PipeReader input = connectionContext.Transport.Input;
        System.IO.Pipelines.ReadResult result = await input.ReadAsync(
            connectionContext.ConnectionClosed
        );
        ReadOnlySequence<byte> buffer = result.Buffer;

        bool looksLikeTls = buffer.Length > 0 && buffer.FirstSpan[0] == TlsHandshakeContentType;
        if (result.IsCompleted || buffer.IsEmpty || looksLikeTls)
        {
            // Nothing consumed AND nothing examined. Examining the ClientHello parks
            // the pipe until bytes arrive beyond it, and none ever do: the client is
            // waiting on our ServerHello. Every TLS connection hung on that.
            input.AdvanceTo(buffer.Start, buffer.Start);
            await next(connectionContext);
            return;
        }

        string? host = ParseHostHeader(buffer);
        string path = ParseRequestPath(buffer);
        input.AdvanceTo(buffer.Start, buffer.End);

        // The NoMercy API checks reachability over plain http, because the same check must
        // work before a certificate exists. A redirect would read as "unreachable".
        string? reachCode = ReachCodes.Shared.Match(path);

        byte[] response = reachCode is not null
            ? ReachCodes.HttpAnswer(reachCode)
            : Encoding.ASCII.GetBytes(
                "HTTP/1.1 301 Moved Permanently\r\n"
                    + $"Location: https://{host ?? "localhost"}:{httpsPort}{path}\r\n"
                    + "Connection: close\r\n"
                    + "Content-Length: 0\r\n"
                    + "\r\n"
            );

        await connectionContext.Transport.Output.WriteAsync(
            response,
            connectionContext.ConnectionClosed
        );
        await connectionContext.Transport.Output.CompleteAsync();
        await input.CompleteAsync();
    }

    internal static string? ParseHostHeader(ReadOnlySequence<byte> buffer)
    {
        string text = Encoding.ASCII.GetString(buffer);
        foreach (string line in text.Split("\r\n"))
        {
            if (!line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))
                continue;

            string host = line["Host:".Length..].Trim();
            // Strip a plaintext-port suffix (":7626") — the redirect carries its own port.
            int colon = host.LastIndexOf(':');
            return colon > 0 ? host[..colon] : host;
        }

        return null;
    }

    internal static string ParseRequestPath(ReadOnlySequence<byte> buffer)
    {
        string text = Encoding.ASCII.GetString(buffer);
        int firstLineEnd = text.IndexOf("\r\n", StringComparison.Ordinal);
        string requestLine = firstLineEnd >= 0 ? text[..firstLineEnd] : text;

        string[] parts = requestLine.Split(' ');
        return parts.Length >= 2 && parts[1].StartsWith('/') ? parts[1] : "/";
    }
}
