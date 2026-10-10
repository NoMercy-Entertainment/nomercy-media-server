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

namespace NoMercy.Data.Repositories;

public class RecommendationCandidateDto
{
    public int MediaId { get; set; }
    public string? Title { get; set; }
    public string? TitleSort { get; set; }
    public string? Overview { get; set; }
    public string? Poster { get; set; }
    public string? Backdrop { get; set; }
    public string ColorPalette { get; set; } = string.Empty;
    public string MediaType { get; set; } = string.Empty;
    public string? SourceMediaType { get; set; }
    public int SourceCount { get; set; }
    public List<int> SourceIds { get; set; } = [];
}
