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

public class Caption
{
    [JsonProperty("ext")]
    public string? Ext { get; set; }

    [JsonProperty("url")]
    public Uri? Url { get; set; }

    [JsonProperty("name")]
    public string? Name { get; set; }

    [JsonProperty("__yt_dlp_client")]
    public string? YtDlpClient { get; set; }
}
