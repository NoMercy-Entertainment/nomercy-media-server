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

using NoMercy.OpticalMedia.Sources;

namespace NoMercy.OpticalMedia.Rip;

/// <summary>
/// Result of <see cref="IDiscRipPreparationService.PrepareAsync"/>. On failure
/// <see cref="ErrorMessage"/> is set and every other field is null — every failure this
/// service produces maps to a 400 Bad Request, so the controller needs nothing more specific
/// than the message. On success every field except <see cref="ErrorMessage"/> is set.
/// </summary>
public record DiscRipPreparationResult
{
    public required bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public RipRequest? EnrichedRequest { get; init; }
    public Ulid? TargetFolderId { get; init; }
    public Ulid? TargetLibraryId { get; init; }
    public string? TargetLibraryType { get; init; }
    public string? OutputDir { get; init; }
}
