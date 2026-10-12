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

using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace NoMercy.NmSystem.Networking;

/// <summary>
/// One-time codes for the reachability call-back. The NoMercy API calls this server on
/// <c>http://address:port/.well-known/nomercy-reach/{code}</c> and expects the code back.
/// Only the server that issued the code can answer it, so a correct answer proves the call
/// reached THIS server, not another device on the same address. Plain http, because the
/// check must work before a certificate exists and before Kestrel owns the port.
/// </summary>
public sealed class ReachCodes
{
    public const string PathPrefix = "/.well-known/nomercy-reach/";

    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The instance every responder reads: the boot listener, the plaintext sniffer in
    /// front of TLS, and the http pipeline all answer for the same codes.
    /// </summary>
    public static ReachCodes Shared { get; } = new();

    private readonly ConcurrentDictionary<string, DateTime> _codes = new();
    private readonly Func<DateTime> _now;

    public ReachCodes(Func<DateTime>? now = null)
    {
        _now = now ?? (() => DateTime.UtcNow);
    }

    public string Issue()
    {
        string code = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(24));
        _codes[code] = _now() + Lifetime;
        return code;
    }

    public void Revoke(string code) => _codes.TryRemove(code, out _);

    /// <summary>
    /// The code a request path asks for, when this server issued it and still holds it;
    /// otherwise null.
    /// </summary>
    public string? Match(string? path)
    {
        if (path is null || !path.StartsWith(PathPrefix, StringComparison.Ordinal))
            return null;

        string code = path[PathPrefix.Length..];
        int query = code.IndexOf('?');
        if (query >= 0)
            code = code[..query];

        if (!_codes.TryGetValue(code, out DateTime expires))
            return null;

        if (expires < _now())
        {
            _codes.TryRemove(code, out _);
            return null;
        }

        return code;
    }

    /// <summary>The raw HTTP/1.1 answer, for listeners that write bytes themselves.</summary>
    public static byte[] HttpAnswer(string code) =>
        Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\n"
                + "Content-Type: text/plain\r\n"
                + $"Content-Length: {code.Length}\r\n"
                + "Cache-Control: no-store\r\n"
                + "Connection: close\r\n"
                + "\r\n"
                + code
        );
}
