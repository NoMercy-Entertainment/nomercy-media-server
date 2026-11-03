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

using System.Globalization;
using NoMercy.Encoder.Composition;

namespace NoMercy.Encoder.Distribution;

/// <summary>
/// Signs every outgoing request with the shared distributed-encoding key, the
/// way the coordinator's HmacValidationMiddleware checks it: the string to sign
/// is built by <see cref="HmacSigner"/> (method, path, timestamp, body hash) and
/// the result travels in the <see cref="HmacRequestHeaders"/> headers.
///
/// Sits on the worker self-registration client, whose register, heartbeat and
/// unregister calls all go to the protected /api/v1/distribution/ prefix.
/// Without a configured key the request goes out unsigned; the coordinator
/// then refuses it, which is the right answer for a misconfigured worker.
/// </summary>
public sealed class HmacSigningHandler(EncoderOptions options) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        string? secret = options.DistributedEncodingSigningKey;
        if (!string.IsNullOrWhiteSpace(secret) && request.RequestUri is { IsAbsoluteUri: true })
            await SignAsync(request, secret, cancellationToken).ConfigureAwait(false);

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static async Task SignAsync(
        HttpRequestMessage request,
        string secret,
        CancellationToken cancellationToken
    )
    {
        // Reading the content buffers it, so the bytes signed are the bytes sent.
        byte[] body = request.Content is null
            ? []
            : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string signature = new HmacSigner(secret).Sign(
            request.Method.Method,
            request.RequestUri!.AbsolutePath,
            timestamp,
            body
        );

        request.Headers.Remove(HmacRequestHeaders.Timestamp);
        request.Headers.Remove(HmacRequestHeaders.Signature);
        request.Headers.Add(
            HmacRequestHeaders.Timestamp,
            timestamp.ToString(CultureInfo.InvariantCulture)
        );
        request.Headers.Add(HmacRequestHeaders.Signature, signature);
    }
}
