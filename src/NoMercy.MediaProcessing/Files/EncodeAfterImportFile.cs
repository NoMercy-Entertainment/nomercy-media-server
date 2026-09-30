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

namespace NoMercy.MediaProcessing.Files;

/// <summary>
/// A file picked in Add content, carried by the show import that has to run first, so
/// the encode is queued once the show's episode rows exist. Holds what a
/// <c>VideoEncodeJob</c> needs and nothing else.
/// </summary>
public record EncodeAfterImportFile
{
    public string Id { get; set; } = string.Empty;
    public string InputFile { get; set; } = string.Empty;
    public Ulid FolderId { get; set; }
    public Ulid? SourceDriverId { get; set; }
    public Ulid? PresetId { get; set; }
}
