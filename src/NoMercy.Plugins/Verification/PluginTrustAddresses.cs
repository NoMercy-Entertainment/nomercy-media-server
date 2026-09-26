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

namespace NoMercy.PluginSdk.Verification;

/// <summary>Where marketplace trust is fetched from.</summary>
/// <param name="Entitlements">Read per refresh: it carries this server's id.</param>
public sealed record PluginTrustAddresses(Uri Keys, Uri Revocations, Func<Uri> Entitlements);
