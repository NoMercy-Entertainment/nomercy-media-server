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

namespace NoMercy.NmSystem.Configuration;

/// <summary>
/// Which server builds this install is offered as updates. Each channel also
/// accepts everything above it: beta offers betas and stables, nightly offers
/// every build.
/// </summary>
public enum ReleaseChannel
{
    Stable,
    Beta,
    Nightly,
}
