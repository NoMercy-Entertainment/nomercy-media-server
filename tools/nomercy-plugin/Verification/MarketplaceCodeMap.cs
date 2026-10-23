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

using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.Plugin.Cli;

/// <summary>
/// The marketplace refusal each CLI code becomes. Null means the marketplace has
/// no refusal of that kind: the finding is still reported, and a failed run with
/// no mapped finding is read by the marketplace as an unreadable scan.
/// </summary>
public static class MarketplaceCodeMap
{
    public static string? For(string pluginCode)
    {
        return pluginCode switch
        {
            PluginRefusalCodes.CapabilityNotDeclared => "MARKETPLACE_SCAN_UNDECLARED_CAPABILITY",
            PluginRefusalCodes.CodeScan => "MARKETPLACE_SCAN_BANNED_API",
            _ => null,
        };
    }
}
