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
/// One <see cref="IPluginAudioTools.RenderAsync" />: the inputs, the graph
/// that combines them and the files its output pads are encoded to. Added in
/// ABI 12.7.
/// </summary>
/// <param name="Inputs">
/// One to <see cref="MaxInputs" /> inputs. The graph addresses them by
/// position: <c>[0:a]</c> is the first, <c>[1:a]</c> the second.
/// </param>
/// <param name="GraphText">
/// The filter graph in ffmpeg's own syntax, always run as a complex graph, so
/// its inputs and outputs are labelled:
/// <c>"[0:a][1:a]amix=inputs=2[mix]"</c>. The tokens in
/// <see cref="PluginFilterGraphTokens" /> are replaced the way they are for
/// <see cref="PluginFilterGraph.Text" />.
/// </param>
/// <param name="Outputs">One to <see cref="MaxOutputs" /> files, each fed by one output pad of the graph.</param>
public sealed record PluginAudioRender(
    IReadOnlyList<PluginAudioSource> Inputs,
    string GraphText,
    IReadOnlyList<PluginAudioOutput> Outputs
)
{
    /// <summary>The most inputs one render may read.</summary>
    public const int MaxInputs = 8;

    /// <summary>The most files one render may write.</summary>
    public const int MaxOutputs = 8;
}
