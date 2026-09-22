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

using NoMercy.NmSystem.Dto;

namespace NoMercy.PluginSdk.Hooks;

/// <summary>
/// The files plugins contribute to a scan, on top of what the scanner found.
/// <para>
/// Additive and on disk. A plugin returns paths, the scan runs them through the
/// same parser and probe every native file goes through, and they are stored the
/// same way — because a file that skipped the parser has no episode, no tags and
/// no duration, and the store filters it straight back out.
/// </para>
/// <para>
/// A source with no file behind it — a radio stream, a remote catalogue — cannot
/// arrive this way and is not meant to: those are screens, and they reach a
/// viewer through the plugin's own UI.
/// </para>
/// </summary>
public interface IPluginMediaSourceProvider
{
    /// <summary>
    /// Every plugin's answer for one folder, already converted to the shape the
    /// scan works in. Empty when no plugin declares the hook.
    /// </summary>
    Task<IReadOnlyList<MediaFolderExtend>> ScanAsync(string path, CancellationToken ct = default);
}
