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
/// What ffmpeg reads: a library track by id, or a file in the plugin's own
/// data folder by relative path. Exactly one of the two is set, and the
/// factory methods below are the only way to build one, so a caller never has
/// to weigh which id a filter graph is meant to run against.
/// </summary>
/// <param name="TrackId">Set by <see cref="Track" />; null otherwise.</param>
public sealed record PluginAudioInput(string? TrackId)
{
    /// <summary>
    /// Set by <see cref="PluginFile" />; null otherwise. Added in ABI 12.7.
    /// </summary>
    public string? PluginFilePath { get; init; }

    /// <summary>A track already in the library, addressed by its id.</summary>
    public static PluginAudioInput Track(string trackId) => new(trackId);

    /// <summary>
    /// A file in the plugin's own data folder (<c>context.Storage.Private</c>),
    /// by its path relative to that folder: something the plugin downloaded,
    /// or the output of an earlier <see cref="IPluginAudioTools.RenderAsync" />.
    /// An absolute path, a <c>..</c> segment and a path that leaves the folder
    /// are refused. Added in ABI 12.7.
    /// </summary>
    public static PluginAudioInput PluginFile(string relativePath) =>
        new((string?)null) { PluginFilePath = relativePath };
}
