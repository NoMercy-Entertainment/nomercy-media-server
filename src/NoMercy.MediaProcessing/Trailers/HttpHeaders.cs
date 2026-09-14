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
using NoMercy.Providers.Helpers;

namespace NoMercy.MediaProcessing.Trailers;

public class HttpHeaders
{
    [JsonProperty("User-Agent")]
    public string? UserAgent { get; set; }

    [JsonProperty("Accept")]
    public string? Accept { get; set; }

    [JsonProperty("Accept-Language")]
    public string? AcceptLanguage { get; set; }

    [JsonProperty("Sec-Fetch-Mode")]
    public string? SecFetchMode { get; set; }
}
