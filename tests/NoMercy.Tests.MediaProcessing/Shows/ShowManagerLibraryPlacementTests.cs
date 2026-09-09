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

using FluentAssertions;
using NoMercy.MediaProcessing.Shows;
using Xunit;

namespace NoMercy.Tests.MediaProcessing.Shows;

public class ShowManagerLibraryPlacementTests
{
    // Regression: a show scanned into a dedicated anime library must never be
    // evicted to "tv" because AniList/Jikan title matching missed it - the
    // folder it was scanned from is a stronger signal than a title-match miss.
    [Fact]
    public void ShouldPromoteToAnimeLibrary_TvVerdictInAnimeLibrary_ReturnsFalse()
    {
        ShowManager
            .ShouldPromoteToAnimeLibrary(scannedLibraryType: "anime", mediaType: "tv")
            .Should()
            .BeFalse();
    }

    // An inconclusive lookup (both providers erroring) must not evict either.
    [Fact]
    public void ShouldPromoteToAnimeLibrary_InconclusiveVerdictInAnimeLibrary_ReturnsFalse()
    {
        ShowManager
            .ShouldPromoteToAnimeLibrary(scannedLibraryType: "anime", mediaType: null)
            .Should()
            .BeFalse();
    }

    // A show already sitting in the anime library never needs re-resolving,
    // even when the classifier agrees.
    [Fact]
    public void ShouldPromoteToAnimeLibrary_AnimeVerdictAlreadyInAnimeLibrary_ReturnsFalse()
    {
        ShowManager
            .ShouldPromoteToAnimeLibrary(scannedLibraryType: "anime", mediaType: "anime")
            .Should()
            .BeFalse();
    }

    // The one case that must still promote: a show scanned into a general tv
    // library that the classifier positively confirms as anime.
    [Fact]
    public void ShouldPromoteToAnimeLibrary_AnimeVerdictInTvLibrary_ReturnsTrue()
    {
        ShowManager
            .ShouldPromoteToAnimeLibrary(scannedLibraryType: "tv", mediaType: "anime")
            .Should()
            .BeTrue();
    }

    // A "tv" or inconclusive verdict in a general tv library is a no-op
    // either way - it must not attempt any reclassification.
    [Fact]
    public void ShouldPromoteToAnimeLibrary_TvVerdictInTvLibrary_ReturnsFalse()
    {
        ShowManager
            .ShouldPromoteToAnimeLibrary(scannedLibraryType: "tv", mediaType: "tv")
            .Should()
            .BeFalse();
    }
}
