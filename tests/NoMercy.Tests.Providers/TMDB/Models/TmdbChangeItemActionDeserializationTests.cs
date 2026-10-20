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

using NoMercy.NmSystem.NewtonSoftConverters;
using NoMercy.Providers.TMDB.Models.Season;
using NoMercy.Providers.TMDB.Models.TV;

namespace NoMercy.Tests.Providers.TMDB.Models;

/// <summary>
/// TMDB reports a change item's <c>action</c> as a string ("added", "updated",
/// "deleted"). The season and TV change items must read it the way the episode,
/// movie and person change items already do, through the same
/// <see cref="JsonHelper.FromJson{T}"/> path the clients use.
/// </summary>
[Trait("Category", "Unit")]
public class TmdbChangeItemActionDeserializationTests
{
    private const string SeasonAppendsWithChanges = """
        {
          "id": 3572,
          "name": "Season 1",
          "season_number": 1,
          "changes": {
            "changes": [
              {
                "key": "overview",
                "items": [
                  {
                    "id": "65a1b2c3d4e5f60007a1b2c3",
                    "action": "added",
                    "time": "2024-01-12 10:15:00 UTC",
                    "iso_639_1": "en",
                    "value": "A chemistry teacher turns to crime.",
                    "original_value": ""
                  }
                ]
              }
            ]
          }
        }
        """;

    private const string TvChangesWithItem = """
        {
          "key": "overview",
          "items": [
            {
              "id": "65a1b2c3d4e5f60007a1b2c4",
              "action": "added",
              "time": "2024-01-12 10:15:00 UTC",
              "iso_639_1": "en",
              "value": "A chemistry teacher turns to crime.",
              "original_value": ""
            }
          ]
        }
        """;

    [Fact]
    public void TmdbSeasonAppends_ChangeItemAction_IsReadAsString()
    {
        TmdbSeasonAppends? appends = SeasonAppendsWithChanges.FromJson<TmdbSeasonAppends>();

        appends.Should().NotBeNull();
        appends!.Changes.Should().NotBeNull();
        TmdbSeasonChangeItem item = appends
            .Changes!.Changes.Should()
            .ContainSingle()
            .Which.Items.Should()
            .ContainSingle()
            .Which;
        item.Action.Should().Be("added");
        item.Time.Should().Be("2024-01-12 10:15:00 UTC");
    }

    [Fact]
    public void TmdbTvChanges_ChangeItemAction_IsReadAsString()
    {
        TmdbTvChanges? changes = TvChangesWithItem.FromJson<TmdbTvChanges>();

        changes.Should().NotBeNull();
        TmdbTvChangeItem item = changes!.Items.Should().ContainSingle().Which;
        item.Action.Should().Be("added");
        item.Value.Should().Be("A chemistry teacher turns to crime.");
    }
}
