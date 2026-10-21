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

namespace NoMercy.PluginSdk;

/// <summary>
/// Builds the host's <see cref="IPluginLyrics" /> for one plugin. Never
/// returns null: whether the plugin declared
/// <c>PluginHookCapability.LyricsWrite</c> is checked by the plugin host before
/// the facade is handed out, not re-checked here. Built per call, like the
/// analysis writer: it only stamps the plugin id.
/// </summary>
public interface IPluginLyricsFactory
{
    IPluginLyrics CreateFor(Ulid pluginId);
}
