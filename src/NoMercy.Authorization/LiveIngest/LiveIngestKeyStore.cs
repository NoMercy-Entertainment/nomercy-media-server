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
using System.Security.Cryptography;

using NoMercy.Authorization.LiveIngest;
namespace NoMercy.Authorization.LiveIngest;

/// <inheritdoc />
public class LiveIngestKeyStore : ILiveIngestKeyStore
{
    // An ingest key must outlive any single playback session's whole run so a
    // long transcode's later range requests never 401 mid-stream — the failure
    // mode a short-lived Keycloak token caused. It still never lingers: the idle
    // reaper caps a session well under this, EndSessionAsync revokes explicitly,
    // and this absolute ceiling only backstops a session that dies without a
    // clean teardown. Comfortably longer than the longest single file.
    private static readonly TimeSpan AbsoluteLifetime = TimeSpan.FromHours(12);

    private sealed record Entry(string PathPrefix, DateTime ExpiresAtUtc, string? SessionId);

    private readonly ConcurrentDictionary<string, Entry> _byKey = new(StringComparer.Ordinal);

    public string Issue(string servedPath)
    {
        PruneExpired();

        // Authorize the served file's whole folder, not just the exact file. A
        // NoMercy-encoded source is an HLS master (".NoMercy.m3u8") whose variant
        // playlists, segments, audio renditions and subtitles all live under the
        // same episode/movie folder, and ffmpeg's self-ingest follows every one of
        // those nested URLs. Scoping to the file alone 401s them all. The folder is
        // one title's content — still far tighter than the old library-wide bearer,
        // and the request is loopback + ingest-port gated regardless.
        int lastSlash = servedPath.LastIndexOf('/');
        string prefix = lastSlash >= 0 ? servedPath[..(lastSlash + 1)] : servedPath;

        // 256 bits of entropy, base64url so the value is safe to drop verbatim
        // into an ffmpeg "-headers" line (no CR/LF, no reserved chars).
        string key = Convert
            .ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

        _byKey[key] = new(prefix, DateTime.UtcNow.Add(AbsoluteLifetime), SessionId: null);
        return key;
    }

    public void BindSession(string key, string sessionId)
    {
        if (_byKey.TryGetValue(key, out Entry? entry))
            _byKey[key] = entry with { SessionId = sessionId };
    }

    public bool TryValidate(string key, string requestPath)
    {
        if (string.IsNullOrEmpty(key) || !_byKey.TryGetValue(key, out Entry? entry))
            return false;

        if (DateTime.UtcNow > entry.ExpiresAtUtc)
        {
            _byKey.TryRemove(key, out _);
            return false;
        }

        // The request path must sit under the authorized folder prefix. The
        // serving middleware decodes the path, so match both the raw request path
        // and its decoded form — the prefix ends in '/', so this is a true folder
        // boundary, not a "/dir" matching "/dir-other" substring slip.
        string decoded = Uri.UnescapeDataString(requestPath);
        return requestPath.StartsWith(entry.PathPrefix, StringComparison.Ordinal)
            || decoded.StartsWith(entry.PathPrefix, StringComparison.Ordinal);
    }

    public void RevokeSession(string sessionId)
    {
        foreach (KeyValuePair<string, Entry> kvp in _byKey)
        {
            if (string.Equals(kvp.Value.SessionId, sessionId, StringComparison.Ordinal))
                _byKey.TryRemove(kvp.Key, out _);
        }
    }

    private void PruneExpired()
    {
        DateTime now = DateTime.UtcNow;
        foreach (KeyValuePair<string, Entry> kvp in _byKey)
        {
            if (now > kvp.Value.ExpiresAtUtc)
                _byKey.TryRemove(kvp.Key, out _);
        }
    }
}
