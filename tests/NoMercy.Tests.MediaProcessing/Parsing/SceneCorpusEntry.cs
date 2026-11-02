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

namespace NoMercy.Tests.MediaProcessing.Parsing;

/// <summary>One release name and what its source says it holds; null = not asserted.</summary>
internal sealed record SceneCorpusEntry(
    string Name,
    string Kind,
    string? Title,
    int? Year,
    int? Season,
    int? Episode,
    string Source,
    string? Folder
)
{
    public string SourceRepo => Source.Split('@')[0];
}
