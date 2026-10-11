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

using NoMercy.Data.Repositories;

namespace NoMercy.Api.DTOs.Media.Components;

/// <summary>The served URL of a stored music image.</summary>
public static class MusicCover
{
    /// <summary>Null when there is no cover, empty included.</summary>
    public static string? Url(string? cover) =>
        string.IsNullOrEmpty(cover) ? null : $"/images/music{cover}";

    /// <summary>Null only when the path is null; an empty path still yields the folder URL.</summary>
    public static string? UrlWhenSet(string? path) => path is null ? null : $"/images/music{path}";

    /// <summary>Album covers as stored, turned into the URLs the server serves them on.</summary>
    public static GroupPoster[] Served(IEnumerable<GroupPoster> covers) =>
        [
            .. covers.Select(cover =>
                Url(cover.Src) is { } url ? cover with { Src = url, Url = url } : cover
            ),
        ];
}
