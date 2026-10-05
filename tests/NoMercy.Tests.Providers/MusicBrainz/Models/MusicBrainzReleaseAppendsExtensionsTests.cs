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
using NoMercy.Providers.MusicBrainz.Models;

namespace NoMercy.Tests.Providers.MusicBrainz.Models;

/// <summary>
/// The one place every Year-resolution call site (folder naming, the stored
/// encode year, the library-scan import path, and the recorded track date)
/// reads from, after four independent copies of this fallback chain drifted
/// out of sync and each missed a different rung — see the fix's commit
/// message for the full incident.
/// </summary>
[Trait("Category", "Unit")]
public class MusicBrainzReleaseAppendsExtensionsTests
{
    private static MusicBrainzReleaseAppends ReleaseFrom(
        string? date,
        string? firstReleaseEventDate,
        string? firstReleaseDate
    ) =>
        JsonConvert.DeserializeObject<MusicBrainzReleaseAppends>(
            $$"""
            {
              "title": "Test Release",
              "date": {{(date is null ? "null" : $"\"{date}\"")}},
              "release-events": [
                { "date": {{(
                firstReleaseEventDate is null ? "null" : $"\"{firstReleaseEventDate}\""
            )}} }
              ],
              "release-group": { "first-release-date": {{(
                firstReleaseDate is null ? "null" : $"\"{firstReleaseDate}\""
            )}} }
            }
            """
        )!;

    [Fact]
    public void OwnDate_IsPreferredOverEverything()
    {
        MusicBrainzReleaseAppends release = ReleaseFrom("2009-01-01", "1987-07-21", "1985-01-01");

        release.ResolvedYear().Should().Be(2009);
    }

    [Fact]
    public void OwnDateMissing_FallsBackToFirstReleaseEvent()
    {
        MusicBrainzReleaseAppends release = ReleaseFrom(null, "1987-07-21", "1985-01-01");

        release.ResolvedYear().Should().Be(1987);
    }

    [Fact]
    public void OwnDateAndEventsMissing_FallsBackToReleaseGroupDate()
    {
        MusicBrainzReleaseAppends release = ReleaseFrom(null, null, "1985-01-01");

        release.ResolvedYear().Should().Be(1985);
    }

    [Fact]
    public void NoDateAnywhere_YearIsNull()
    {
        MusicBrainzReleaseAppends release = new() { Title = "Undated Bootleg" };

        release.ResolvedYear().Should().BeNull();
    }
}
