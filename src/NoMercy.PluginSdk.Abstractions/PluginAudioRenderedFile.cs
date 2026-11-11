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
/// One file a finished <see cref="IPluginAudioTools.RenderAsync" /> left in
/// the plugin's data folder. Added in ABI 12.7.
/// </summary>
/// <param name="RelativePath">
/// The path the plugin asked for in <see cref="PluginAudioOutput.RelativePath" />,
/// unchanged, so it can be handed straight to <c>Media.Proxy.MintFileAsync</c>
/// or back to <c>Storage.Private</c>.
/// </param>
/// <param name="Bytes">The size of the file on disk.</param>
public sealed record PluginAudioRenderedFile(string RelativePath, long Bytes);
