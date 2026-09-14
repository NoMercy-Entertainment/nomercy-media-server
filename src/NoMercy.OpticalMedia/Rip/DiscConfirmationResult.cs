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

namespace NoMercy.OpticalMedia.Rip;

/// <summary>
/// Result of <see cref="IDiscConfirmationService.ConfirmAsync"/>. <see cref="Destination"/>
/// is only set when <see cref="Outcome"/> is <see cref="DiscConfirmationOutcome.Confirmed"/>.
/// </summary>
public record DiscConfirmationResult(DiscConfirmationOutcome Outcome, string? Destination = null);
