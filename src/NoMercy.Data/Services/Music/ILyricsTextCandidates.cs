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

namespace NoMercy.Data.Services.Music;

/// <summary>
/// Every lyric text the host can find for a track: each validated provider
/// candidate and the file's embedded lyrics, cached per track for a day.
/// </summary>
public interface ILyricsTextCandidates
{
    Task<PluginLyricsText> GetAsync(Guid trackId, CancellationToken ct = default);
}
