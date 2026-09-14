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
using NoMercy.Database;
using NoMercy.Database.Models.Media;
using NoMercy.Database.Models.TvShows;
using NoMercy.Database.Models.Users;
using NoMercy.NmSystem.Extensions;

namespace NoMercy.Api.DTOs.Media;

public class MissingEpisodeDto(Episode episode) : EpisodeDto(episode)
{
    private readonly Episode _episode = episode;

    [JsonProperty("link")]
    public new Uri Link =>
        new(
            $"https://www.themoviedb.org/tv/{_episode.TvId}/season/{_episode.SeasonNumber}/episode/{_episode.EpisodeNumber}",
            UriKind.Absolute
        );

    [JsonProperty("available")]
    public new bool Available => true;
}
