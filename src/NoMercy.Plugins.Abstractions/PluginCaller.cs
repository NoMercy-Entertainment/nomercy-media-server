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

namespace NoMercy.Plugins.Abstractions;

/// <summary>
/// Who is asking. A caller with no access never reaches the plugin: the server
/// answers 403 before dispatch, which is the enforcement point for rule 2.6.1.
/// </summary>
public sealed record PluginCaller(
    UserId Id,
    string DisplayName,
    PluginRole Role,
    PluginAccess Access,
    string Locale,
    string Surface
);
