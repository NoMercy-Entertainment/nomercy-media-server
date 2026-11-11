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
/// One file <see cref="IPluginAudioTools.RenderAsync" /> writes: which output
/// pad of the graph it takes, where in the plugin's data folder it lands and
/// how it is encoded. Added in ABI 12.7.
/// </summary>
/// <param name="Pad">
/// An output label of the graph, written without brackets: <c>"mix"</c> for a
/// graph that ends in <c>[mix]</c>. Each output takes a pad of its own.
/// </param>
/// <param name="RelativePath">
/// Where the file lands, relative to the plugin's data folder
/// (<c>context.Storage.Private</c>), with forward slashes. Folders on the way
/// are created. A file already there is replaced once the run has succeeded,
/// never before. An absolute path, a <c>..</c> segment and a path that leaves
/// the folder are refused before ffmpeg starts.
/// </param>
/// <param name="Codec">The codec, which also decides the container.</param>
/// <param name="BitrateKbps">
/// <see cref="PluginAudioCodec.Opus" /> only: the bitrate in kbit/s, 6 to 510.
/// Null leaves it to the encoder. Refused on a lossless codec, which has no
/// bitrate to set.
/// </param>
/// <param name="SampleRate">
/// The sample rate in Hz. Null keeps what the graph produced. Opus encodes at
/// 8000, 12000, 16000, 24000 or 48000 only; any other rate is refused for it.
/// </param>
public sealed record PluginAudioOutput(
    string Pad,
    string RelativePath,
    PluginAudioCodec Codec,
    int? BitrateKbps = null,
    int? SampleRate = null
);
