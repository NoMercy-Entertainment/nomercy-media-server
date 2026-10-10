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

using System.Text;
using Newtonsoft.Json;
using NoMercy.Providers.AniList.Models;
using NoMercy.Providers.Helpers;

namespace NoMercy.Providers.AniList;

public static class AniListClient
{
    private const string SearchQuery = """
        query ($search: String, $seasonYear: Int) {
          Page(page: 1, perPage: 5) {
            media(search: $search, seasonYear: $seasonYear, type: ANIME) {
              id
              idMal
              title { romaji english native }
              synonyms
              countryOfOrigin
              seasonYear
              season
              genres
              tags { name category isAdult }
            }
          }
        }
        """;

    public static async Task<AniListMedia?> SearchAsync(HttpClient client, string title, int? year)
    {
        return await SearchWithPriorityAsync(client, title, year, false);
    }

    internal static async Task<AniListMedia?> SearchWithPriorityAsync(
        HttpClient client,
        string title,
        int? year,
        bool? priority
    )
    {
        try
        {
            return await ProviderQueues
                .For(HttpClientNames.AniList)
                .Enqueue(
                    () => SearchCoreAsync(client, title, year),
                    $"anilist-search-{title}-{year}",
                    priority
                );
        }
        catch (HttpRequestException ex)
            when (ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                || (int?)ex.StatusCode is >= 500 and <= 599
            )
        {
            return null;
        }
    }

    private static async Task<AniListMedia?> SearchCoreAsync(
        HttpClient client,
        string title,
        int? year
    )
    {
        object payload = new
        {
            query = SearchQuery,
            variables = new { search = title, seasonYear = year },
        };

        using HttpRequestMessage request = new(HttpMethod.Post, client.BaseAddress)
        {
            Content = new StringContent(
                JsonConvert.SerializeObject(payload),
                Encoding.UTF8,
                "application/json"
            ),
        };

        using HttpResponseMessage response = await client.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            if (
                response.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                || (int)response.StatusCode >= 500
            )
                response.EnsureProviderSuccess();
            return null;
        }

        string body = await response.Content.ReadAsStringAsync();
        AniListResponse? parsed = JsonConvert.DeserializeObject<AniListResponse>(body);

        return parsed?.Data.Page.Media.FirstOrDefault();
    }
}
