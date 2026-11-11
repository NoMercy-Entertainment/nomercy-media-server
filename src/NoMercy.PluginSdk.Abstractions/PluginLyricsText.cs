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
/// <para>
/// An empty list is only "this track has no text" when
/// <see cref="IsComplete" /> is true. When it is false a source could not be
/// asked, and the same question asked later may well get a text.
/// </para>
/// </summary>
public sealed record PluginLyricsText(IReadOnlyList<PluginTextCandidate> Candidates)
{
    /// <summary>
    /// False when a source could not be asked (a provider failed or is backed
    /// off, or the host failed): the list may lack a text that exists. What
    /// the host did find, such as the lyrics embedded in the file, is still
    /// in the list. A property and not a constructor parameter, so every
    /// answer built before it means a complete one. Added in ABI 12.8.
    /// </summary>
    public bool IsComplete { get; init; } = true;

    /// <summary>
    /// When an incomplete answer is worth asking again; null when it is
    /// complete or the host cannot say. Added in ABI 12.8.
    /// </summary>
    public DateTimeOffset? AskAgainAfter { get; init; }
}
