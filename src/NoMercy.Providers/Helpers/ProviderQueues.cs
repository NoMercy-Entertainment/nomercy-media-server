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

using System.Collections.Concurrent;

namespace NoMercy.Providers.Helpers;

public static class ProviderQueues
{
    private static readonly ConcurrentDictionary<string, Queue> Queues = new();

    // All provider limits are specified here. Formerly a 1000 ms batch interval
    // admitted Concurrent requests at once; spacing starts by 1000 / Concurrent
    // preserves that budget without a burst. Retries use the same slots.
    public static QueueOptions OptionsFor(string name) =>
        Family(name) switch
        {
            HttpClientNames.Tmdb or HttpClientNames.Tvdb or HttpClientNames.NoMercyImage => new()
            {
                Concurrent = 50,
                Interval = 20,
            },
            HttpClientNames.AcoustId or HttpClientNames.CoverArt or HttpClientNames.FanArt => new()
            {
                Concurrent = 3,
                Interval = 334,
            },
            HttpClientNames.MusixMatch or HttpClientNames.Tadb => new()
            {
                Concurrent = 2,
                Interval = 500,
            },
            HttpClientNames.MusicBrainz => new() { Concurrent = 1, Interval = 1500 },
            HttpClientNames.AniList => new() { Concurrent = 1, Interval = 2000 },
            HttpClientNames.Jikan => new() { Concurrent = 1, Interval = 350 },
            _ => new() { Concurrent = 1, Interval = 1000 },
        };

    public static Queue For(string name, QueueOptions? options = null) =>
        Queues.GetOrAdd(Family(name), family => new(options ?? OptionsFor(family)));

    private static string Family(string name) =>
        name switch
        {
            HttpClientNames.TmdbImage => HttpClientNames.Tmdb,
            HttpClientNames.TvdbLogin => HttpClientNames.Tvdb,
            HttpClientNames.FanArtImage => HttpClientNames.FanArt,
            HttpClientNames.CoverArtImage => HttpClientNames.CoverArt,
            _ => name,
        };
}
