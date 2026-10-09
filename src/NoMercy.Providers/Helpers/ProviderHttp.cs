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

namespace NoMercy.Providers.Helpers;

public static class ProviderHttp
{
    public static async Task<string> GetStringAsync(HttpClient client, string url)
    {
        using HttpResponseMessage response = await client.GetAsync(url);
        response.EnsureProviderSuccess();
        return await response.Content.ReadAsStringAsync();
    }

    public static void EnsureProviderSuccess(this HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
            return;

        HttpRequestException failure = new(
            $"Provider returned {(int)response.StatusCode} ({response.ReasonPhrase})",
            null,
            response.StatusCode
        );
        if (response.Headers.RetryAfter?.Delta is TimeSpan delta)
            failure.Data["RetryAfter"] = delta;
        else if (response.Headers.RetryAfter?.Date is DateTimeOffset date)
            failure.Data["RetryAfter"] = date - DateTimeOffset.UtcNow;
        throw failure;
    }
}
