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

using NoMercy.Data.Services;
using NoMercy.Providers.AcoustId.Models;

namespace NoMercy.Tests.MediaProcessing.Music;

/// <summary>
/// Issue #396: a recording pressed on several releases was given up on at the first
/// release whose track count differed from the album folder, so a matching release
/// listed after it was never imported.
/// </summary>
[Trait("Category", "Unit")]
public sealed class MusicLogicReleaseTrackCountTests
{
    [Fact]
    public void A_release_with_another_track_count_is_skipped_and_the_matching_one_kept()
    {
        AcoustIdFingerprintReleaseGroups twelveTracks = ReleaseWith(12);
        AcoustIdFingerprintReleaseGroups tenTracks = ReleaseWith(10);
        AcoustIdFingerprintRecording recording = new() { Releases = [twelveTracks, tenTracks] };

        MusicLogic
            .ReleasesMatchingFolder(recording, 10)
            .Should()
            .ContainSingle("the 12-track pressing is skipped, not the end of the file")
            .Which.Should()
            .BeSameAs(tenTracks);
    }

    [Fact]
    public void A_recording_with_no_release_of_the_folder_track_count_matches_nothing()
    {
        AcoustIdFingerprintRecording recording = new()
        {
            Releases = [ReleaseWith(12), ReleaseWith(null)],
        };

        MusicLogic.ReleasesMatchingFolder(recording, 10).Should().BeEmpty();
    }

    private static AcoustIdFingerprintReleaseGroups ReleaseWith(int? trackCount) =>
        new() { Id = Guid.NewGuid(), TrackCount = trackCount };
}
