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

namespace NoMercy.Authorization.LiveIngest;

/// <summary>
/// Mints and validates single-use ingest keys for the transcoder's loopback
/// self-ingest. When ffmpeg/ffprobe pull a library source over the server's own
/// internal HTTP port, they present one of these keys instead of the viewer's
/// Keycloak bearer. A key is scoped to exactly one served file, keeps the user's
/// identity token out of ffmpeg's argv, and outlives a short-lived access token
/// so a long transcode's later range requests keep authenticating.
/// </summary>
public interface ILiveIngestKeyStore
{
    /// <summary>
    /// Mint a key authorizing the served file's folder (e.g. a key for
    /// "/{folderId}/Show/Ep/Ep.NoMercy.m3u8" covers everything under
    /// "/{folderId}/Show/Ep/"). An encoded source is an HLS master whose nested
    /// variant playlists, segments and subtitles all sit in that folder, so the
    /// self-ingest must be able to read the whole subtree, not just the master.
    /// </summary>
    string Issue(string servedPath);

    /// <summary>
    /// Tie an issued key to a live session so <see cref="RevokeSession"/> drops
    /// it the moment the session tears down.
    /// </summary>
    void BindSession(string key, string sessionId);

    /// <summary>
    /// True when the key is live, unexpired, and was minted for this exact
    /// request path.
    /// </summary>
    bool TryValidate(string key, string requestPath);

    /// <summary>Drop every key bound to the session.</summary>
    void RevokeSession(string sessionId);
}
