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

namespace NoMercy.Data.Visualizers;

public class VisualizerBundleTexture
{
    [JsonProperty("name")]
    public string Name { get; set; } = "";

    [JsonProperty("bytes")]
    public int Bytes { get; set; }

    [JsonProperty("sha256")]
    public string Sha256 { get; set; } = "";
}
