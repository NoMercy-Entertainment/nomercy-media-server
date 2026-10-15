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

namespace NoMercy.Database.Models.Media;

public static class VideoFileExtensions
{
    public static VideoFile? Newest(
        this IEnumerable<VideoFile> videoFiles,
        Func<VideoFile, bool>? predicate = null
    )
    {
        IEnumerable<VideoFile> candidates = predicate is null
            ? videoFiles
            : videoFiles.Where(predicate);

        return candidates.OrderByDescending(videoFile => videoFile.Id).FirstOrDefault();
    }
}
