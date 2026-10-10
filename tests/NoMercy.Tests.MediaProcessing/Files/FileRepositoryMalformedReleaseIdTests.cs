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

using System.Reflection;
using NoMercy.MediaProcessing.Files;
using NoMercy.MediaProcessing.Jobs.Dto;
using NoMercy.Providers.MusicBrainz.Models;

namespace NoMercy.Tests.MediaProcessing.Files;

[Trait("Category", "Unit")]
public class FileRepositoryMalformedReleaseIdTests
{
    [Fact]
    public async Task Malformed_release_id_does_not_abort_folder_matching()
    {
        AudioTagModel audioTagModel = new()
        {
            Tags = new TagLib.Id3v2.Tag { MusicBrainzReleaseId = "abc" },
        };
        MethodInfo method = typeof(FileRepository).GetMethod(
            "FromMusicBrainzRelease",
            BindingFlags.NonPublic | BindingFlags.Static
        )!;

        Task task = (Task)
            method.Invoke(
                null,
                [null, audioTagModel, new object(), new List<MusicBrainzReleaseAppends>(), "", "0"]
            )!;

        await task;
    }
}
