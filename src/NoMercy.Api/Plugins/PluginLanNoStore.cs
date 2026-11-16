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

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;

namespace NoMercy.Api.Plugins;

/// <summary>
/// Keeps every LAN answer out of every cache, whatever the plugin wrote.
/// <para>
/// The server's response cache runs before routing and keys an entry by the
/// address, which for a LAN route holds the credential. An answer it stored
/// would be served to the next request for that address without the network
/// check, the credential check or a revocation ever running again.
/// </para>
/// <para>
/// The cache decides whether to store an answer when the first byte is
/// written, which is before any <c>OnStarting</c> callback runs. So the body
/// is wrapped: <c>Cache-Control: no-store</c> is written over the plugin's
/// headers just before the first byte, flush or start reaches the layer
/// below, and once more when the action ends without a body.
/// </para>
/// </summary>
public static class PluginLanNoStore
{
    /// <summary>Wraps the response body so no-store is the last word on caching.</summary>
    public static void Install(HttpContext context)
    {
        IHttpResponseBodyFeature? below = context.Features.Get<IHttpResponseBodyFeature>();

        if (below is null)
            return;

        context.Features.Set<IHttpResponseBodyFeature>(
            new StreamResponseBodyFeature(new Stamping(below.Stream, context.Response), below)
        );

        // Registered before the action runs, so it runs after any callback
        // the action registers: what the device reads says no-store too.
        context.Response.OnStarting(
            static state =>
            {
                Seal((HttpResponse)state);
                return Task.CompletedTask;
            },
            context.Response
        );
    }

    /// <summary>Writes no-store over whatever caching headers are there, while they can still change.</summary>
    public static void Seal(HttpResponse response)
    {
        if (response.HasStarted)
            return;

        response.Headers.CacheControl = "no-store";
        response.Headers.Pragma = "no-cache";
        response.Headers.Remove(HeaderNames.Expires);
    }

    /// <summary>The body, stamping no-store before anything reaches the layer below.</summary>
    private sealed class Stamping(Stream inner, HttpResponse response) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
            Seal(response);
            inner.Flush();
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            Seal(response);
            return inner.FlushAsync(cancellationToken);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            Seal(response);
            inner.Write(buffer, offset, count);
        }

        public override Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken
        )
        {
            Seal(response);
            return inner.WriteAsync(buffer, offset, count, cancellationToken);
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default
        )
        {
            Seal(response);
            return inner.WriteAsync(buffer, cancellationToken);
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
