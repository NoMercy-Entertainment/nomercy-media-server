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
/// An ffmpeg filter chain, written by the plugin and run by the host so a
/// plugin never has to know where the server's ffmpeg build lives, what
/// flags it needs to find a model file, or how to invoke it safely.
/// </summary>
/// <param name="Text">
/// The filter chain itself, in ffmpeg's own syntax - for example
/// <c>"stereotools=mlev=0"</c>. The literal token <c>"{stemsModel}"</c>,
/// where a filter needs one, is replaced by the host with the model file's
/// name before the graph runs, so a plugin never hard-codes a path that only
/// exists on the machine running the server.
/// </param>
/// <param name="Complex">
/// False runs <see cref="Text" /> as <c>-af</c>, a single input to a single
/// output. True runs it as <c>-filter_complex</c>, with the untouched input
/// also mapped to the null muxer so ffmpeg does not refuse to run against an
/// output stream nothing consumes.
/// </param>
public sealed record PluginFilterGraph(string Text, bool Complex);
