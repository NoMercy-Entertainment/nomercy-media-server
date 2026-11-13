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

namespace NoMercy.NmSystem.Domain;

/// <summary>The file extensions the watcher, the inbox scan and the inbox classifier accept.</summary>
public static class MediaFileExtensions
{
    public static readonly string[] Video = [".mp4", ".mkv", ".avi", ".webm", ".mov", ".m3u8"];
    public static readonly string[] Audio = [".mp3", ".flac", ".opus", ".wav", ".m4a"];
    public static readonly string[] Inbox = [.. Video, .. Audio];
}
