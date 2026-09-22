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

using System.Runtime.CompilerServices;

// The host platform, which mints what a plugin is only allowed to receive. A
// plugin assembly is not on this list and cannot be added to it by shipping
// one, so the shapes with no reachable constructor stay that way.
[assembly: InternalsVisibleTo("NoMercy.Plugins")]
