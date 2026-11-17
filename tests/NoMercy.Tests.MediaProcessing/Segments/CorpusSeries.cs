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

namespace NoMercy.Tests.MediaProcessing.Segments;

internal sealed class CorpusSeries
{
    [JsonProperty("series")]
    public string Series { get; set; } = string.Empty;

    [JsonProperty("library_type")]
    public string LibraryType { get; set; } = string.Empty;

    [JsonProperty("episodes")]
    public List<CorpusEpisode> Episodes { get; set; } = [];
}
