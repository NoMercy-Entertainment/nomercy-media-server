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

namespace NoMercy.Data.Plugins;

/// <summary>
/// One input of a render once the host has opened it: the local path ffmpeg
/// reads, and the window of it the plugin asked for.
/// </summary>
/// <param name="Path">The local path. Never shown to the plugin: it may be a library file.</param>
/// <param name="Start">Where the graph starts reading; null is the beginning.</param>
/// <param name="Duration">How much it reads from there; null is to the end.</param>
internal sealed record PluginAudioRenderInput(string Path, TimeSpan? Start, TimeSpan? Duration);
