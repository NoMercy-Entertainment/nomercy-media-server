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

using NoMercy.Providers.Abstractions;
using NoMercy.Providers.Helpers;
using NoMercy.Setup.Server;

namespace NoMercy.Providers.FanArt.Client;

public class FanArtBaseClient : ExternalApiClient
{
    protected FanArtBaseClient() { }

    protected FanArtBaseClient(Guid id)
        : base(id) { }

    protected override string HttpClientName => HttpClientNames.FanArt;
    protected override Uri BaseUrl => new("https://webservice.fanart.tv/v3/");

    protected override void ConfigureClient(HttpClient client)
    {
        client.DefaultRequestHeaders.Add("api-key", ApiKeyStore.Current.FanArtApiKey);
        if (!string.IsNullOrEmpty(ApiKeyStore.Current.FanArtClientKey))
            client.DefaultRequestHeaders.Add("client-key", ApiKeyStore.Current.FanArtClientKey);
    }
}
