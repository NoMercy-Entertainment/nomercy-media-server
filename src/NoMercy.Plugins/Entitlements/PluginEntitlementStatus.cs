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

namespace NoMercy.PluginSdk.Entitlements;

/// <summary>What nomercy.tv says about a purchase. Only Active runs a plugin.</summary>
public enum PluginEntitlementStatus
{
    // The zero value: a bundle stored before status was read holds nothing.
    Unknown,
    Active,
    Suspended,
}
