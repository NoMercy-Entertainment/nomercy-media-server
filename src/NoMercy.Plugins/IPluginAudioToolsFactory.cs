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

namespace NoMercy.Plugins;

/// <summary>
/// Hands out the host's <see cref="IPluginAudioTools" /> for one plugin.
/// <para>
/// The same instance every time for the same plugin, deliberately: the
/// one-ffmpeg-at-a-time guard lives inside the instance, so a fresh one per
/// call would let a plugin start as many ffmpeg processes as it has call
/// sites. Whether a plugin may have these tools at all is the
/// <c>PluginHookCapability.AudioTools</c> question the plugin host answers
/// before asking for them, not something this factory re-checks.
/// </para>
/// </summary>
public interface IPluginAudioToolsFactory
{
    IPluginAudioTools CreateFor(Ulid pluginId);
}
