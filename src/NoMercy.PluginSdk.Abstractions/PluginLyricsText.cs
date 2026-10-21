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
/// Every lyric text the host could find for a track, for a plugin that aligns
/// text to audio and wants to choose or combine. May be empty. Added in ABI 12.6.
/// </summary>
public sealed record PluginLyricsText(IReadOnlyList<PluginTextCandidate> Candidates);
