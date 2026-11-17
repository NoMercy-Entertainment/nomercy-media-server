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

internal sealed class CorpusEpisode
{
    [JsonProperty("episode")]
    public string Episode { get; set; } = string.Empty;

    [JsonProperty("chapters")]
    public List<CorpusCue> Chapters { get; set; } = [];

    [JsonProperty("expected")]
    public List<CorpusSegment> Expected { get; set; } = [];

    [JsonProperty("story")]
    public List<CorpusSegment> Story { get; set; } = [];
}
