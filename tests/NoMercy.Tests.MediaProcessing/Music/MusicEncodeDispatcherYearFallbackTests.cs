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

using Newtonsoft.Json;
using NoMercy.Database.Music;
using NoMercy.MediaProcessing.Music;
using NoMercy.Providers.MusicBrainz.Models;

namespace NoMercy.Tests.MediaProcessing.Music;

/// <summary>
/// A specific pressing's own "date" is frequently absent on MusicBrainz — most
/// reliably on reissues and digital-only additions — even for a well-known,
/// precisely-dated album. Reading only <c>Release.DateTime</c> folded that
/// straight into PicardNaming's "[0000]" unknown-year placeholder, live-observed
/// renaming hundreds of already-correctly-filed albums (Dire Straits, Coldplay,
/// Guns N' Roses, ...) into "[0000] Title" on a production library rescan.
/// </summary>
[Trait("Category", "Unit")]
public class MusicEncodeDispatcherYearFallbackTests
{
    private static MusicBrainzTrack Track() => new() { Id = Guid.NewGuid() };

    // Deserialized from JSON, matching how the MusicBrainz API response is
    // actually turned into these objects in production — the property setters
    // round-trip a DateTime through ToString()/CurrentCulture and back, which
    // is unrelated fragility this test has no reason to depend on.
    private static MusicBrainzReleaseAppends ReleaseFrom(string? date, string? firstReleaseDate) =>
        JsonConvert.DeserializeObject<MusicBrainzReleaseAppends>(
            $$"""
            {
              "title": "Appetite for Destruction",
              "date": {{(date is null ? "null" : $"\"{date}\"")}},
              "release-group": { "first-release-date": {{(
                firstReleaseDate is null ? "null" : $"\"{firstReleaseDate}\""
            )}} }
            }
            """
        )!;

    [Fact]
    public void ReleaseDateMissing_FallsBackToReleaseGroupFirstReleaseDate()
    {
        MusicBrainzReleaseAppends release = ReleaseFrom(null, "1987-07-21");

        MusicNamingContext context = MusicEncodeDispatcher.NamingContextFor(release, Track());

        context.Year.Should().Be(1987);
    }

    [Fact]
    public void ReleaseDatePresent_IsPreferredOverReleaseGroupDate()
    {
        MusicBrainzReleaseAppends release = ReleaseFrom("2009-01-01", "1987-07-21");

        MusicNamingContext context = MusicEncodeDispatcher.NamingContextFor(release, Track());

        context.Year.Should().Be(2009);
    }

    [Fact]
    public void NeitherDateAvailable_YearIsNull()
    {
        MusicBrainzReleaseAppends release = new() { Title = "Unknown Date Bootleg" };

        MusicNamingContext context = MusicEncodeDispatcher.NamingContextFor(release, Track());

        context.Year.Should().BeNull();
    }
}
