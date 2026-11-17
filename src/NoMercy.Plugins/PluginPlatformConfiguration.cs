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

using NoMercy.Storage;

namespace NoMercy.PluginSdk;

/// <inheritdoc cref="IPluginPlatformConfiguration" />
public sealed class PluginPlatformConfiguration(string pluginsPath, IStorage storage)
    : PluginConfiguration(Path.Combine(pluginsPath, "data", "platform"), storage),
        IPluginPlatformConfiguration;
