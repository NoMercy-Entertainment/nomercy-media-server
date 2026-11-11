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

namespace NoMercy.Data.Plugins;

/// <summary>
/// One output of a render the host has accepted: the pad it takes, how it is
/// encoded, and the two paths it has on disk.
/// </summary>
/// <param name="Pad">The graph's output label, without brackets.</param>
/// <param name="RelativePath">The path as the plugin wrote it, which is what the result reports back.</param>
/// <param name="FinalPath">Where the file ends up, inside the plugin's data folder.</param>
/// <param name="TempPath">
/// Where ffmpeg writes it: the same folder, under a name no finished file
/// has. Its extension is the codec's own, because that is what ffmpeg picks
/// the container from.
/// </param>
/// <param name="Codec">The codec, which also decides the container.</param>
/// <param name="BitrateKbps">Opus only; null leaves it to the encoder.</param>
/// <param name="SampleRate">Null keeps what the graph produced.</param>
internal sealed record PluginAudioRenderTarget(
    string Pad,
    string RelativePath,
    string FinalPath,
    string TempPath,
    PluginAudioCodec Codec,
    int? BitrateKbps,
    int? SampleRate
);
