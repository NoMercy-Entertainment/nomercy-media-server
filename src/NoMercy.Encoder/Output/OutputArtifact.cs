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

using NoMercy.NmSystem.Domain;

namespace NoMercy.Encoder.Output;

/// <summary>
/// A single file produced by an encode job. The dashboard renders the
/// artifact list as a file-tree card on the job detail screen —
/// <see cref="Path"/> is the absolute output path, <see cref="SizeBytes"/>
/// drives the size column, <see cref="Sha256"/> is shown in the integrity
/// panel, and <see cref="MediaType"/> determines the file-type icon.
/// </summary>
/// <param name="Path">Absolute path to the output file on the storage backend.</param>
/// <param name="SizeBytes">File size in bytes at the moment the artifact was catalogued.</param>
/// <param name="Sha256">Lowercase hex-encoded SHA-256 digest of the file content.</param>
/// <param name="MediaType">
/// IANA media type (MIME type) for the file — e.g.
/// <c>application/vnd.apple.mpegurl</c> for HLS playlists,
/// <c>video/mp4</c> for fragmented MP4 segments, <c>text/vtt</c> for
/// WebVTT subtitles. Populated via <see cref="MimeFromPath"/>.
/// </param>
public sealed record OutputArtifact(string Path, long SizeBytes, string Sha256, string MediaType)
{
    /// <summary>
    /// Maps a file extension to its IANA media type. Returns
    /// <c>application/octet-stream</c> for any extension not in the table —
    /// the dashboard treats that as a generic binary download.
    /// </summary>
    public static string MimeFromPath(string path)
    {
        string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".m3u8" => "application/vnd.apple.mpegurl",
            ".mpd" => "application/dash+xml",
            ".mp4" => "video/mp4",
            ".mkv" => "video/x-matroska",
            ".webm" => "video/webm",
            ".ts" => "video/mp2t",
            ".m4s" => "video/iso.segment",
            ".m4v" => "video/x-m4v",
            ".m4a" => "audio/mp4",
            ".aac" => "audio/aac",
            ".mp3" => "audio/mpeg",
            // The three a stem can be written as come from the one stem-format
            // vocabulary: a file this labels one way and a register row labels
            // another is a client handed something else than it asked for.
            ".ogg" => StemFormats.OggContentType,
            "." + StemFormats.Opus => StemFormats.OpusContentType,
            "." + StemFormats.Flac => StemFormats.FlacContentType,
            ".vtt" => "text/vtt",
            ".srt" => "text/plain",
            ".ass" => "text/plain",
            ".ssa" => "text/plain",
            ".ttml" => "application/ttml+xml",
            ".webp" => "image/webp",
            ".jpg" => "image/jpeg",
            ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".json" => "application/json",
            ".ttf" => "font/ttf",
            ".otf" => "font/otf",
            ".woff" => "font/woff",
            ".woff2" => "font/woff2",
            _ => "application/octet-stream",
        };
    }
}
