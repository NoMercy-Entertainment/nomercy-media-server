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

namespace NoMercy.Tests.Repositories;

// Stoney, 2026-10-06: "music is not showing the images". A cover can have a palette and still no
// file on the server (older importers wrote the name without the download), so a palette alone
// sent posters that answer 404.
public class MusicGroupCoversTests
{
    private const string Palette =
        """{"cover":{"dominant":"#112233","primary":"#112233","lightVibrant":"#445566","darkVibrant":"#000000","lightMuted":"#778899","darkMuted":"#000000"}}""";

    private static MusicGroupCovers.CoverRow Row(Guid group, string cover, int day) =>
        new(group, Guid.NewGuid(), new DateTime(2026, 1, day), cover, cover, Palette);

    [Fact]
    public async Task A_cover_without_its_file_is_left_out()
    {
        Guid genre = Guid.NewGuid();
        List<MusicGroupCovers.CoverRow> rows =
        [
            Row(genre, "/a.jpg", 1),
            Row(genre, "/missing.jpg", 2),
            Row(genre, "/c.jpg", 3),
        ];

        Dictionary<Guid, GroupPoster[]> posters = await MusicGroupCovers.PickAsync(
            rows,
            cover => Task.FromResult(cover != "/missing.jpg")
        );

        Assert.Equal(["/a.jpg", "/c.jpg"], posters[genre].Select(poster => poster.Src));
    }
}
