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

using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugins.Capabilities;

public interface IPluginConsentService
{
    bool IsBaseline(PluginCapabilities? capabilities);
    bool HasConsent(Ulid pluginId);

    /// <summary>
    /// Whether a recorded consent still covers what <paramref name="capabilities"/>
    /// asks for. False when there is no consent at all, or when the manifest
    /// has widened past what the owner approved.
    /// </summary>
    bool ConsentCoversCapabilities(Ulid pluginId, PluginCapabilities? capabilities);
    void GrantConsent(Ulid pluginId, PluginCapabilities? capabilities, Version manifestVersion);
    void RevokeConsent(Ulid pluginId);
}
