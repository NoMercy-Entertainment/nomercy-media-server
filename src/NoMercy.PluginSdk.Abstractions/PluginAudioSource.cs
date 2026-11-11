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

namespace NoMercy.PluginSdk.Abstractions;

/// <summary>
/// One input of <see cref="IPluginAudioTools.RenderAsync" />, and the part of
/// it the graph gets to see. Added in ABI 12.7.
/// </summary>
/// <param name="Input">
/// A library track, a derived file or a file in the plugin's own data folder.
/// The plugin names it; the host opens it, so a library path never reaches
/// the plugin.
/// </param>
/// <param name="Start">
/// Where in the input the graph starts reading. Null is the beginning. The
/// seek is exact: the audio is decoded up to this point, not cut at the
/// nearest packet.
/// </param>
/// <param name="Duration">How much of the input the graph reads from <paramref name="Start" />. Null is to the end.</param>
public sealed record PluginAudioSource(
    PluginAudioInput Input,
    TimeSpan? Start = null,
    TimeSpan? Duration = null
);
