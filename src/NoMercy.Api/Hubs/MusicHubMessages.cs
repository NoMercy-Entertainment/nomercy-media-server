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

using NoMercy.Api.DTOs.Music;

namespace NoMercy.Api.Hubs;

/// <summary>
/// The messages the music hub sends to a user's clients about the join
/// between two tracks, by name and by what each carries.
/// <para>
/// The commands a client sends are the hub's public methods, and
/// <c>signalr-contracts.json</c> is written from them. A message the server
/// sends has no method, so it is listed here and written down from this list.
/// </para>
/// </summary>
public static class MusicHubMessages
{
    public const string PrepareCrossfade = "PrepareCrossfade";

    public const string PrepareTransition = "PrepareTransition";

    /// <summary>Each message's name and the type of its payload.</summary>
    public static IReadOnlyDictionary<string, Type> Payloads { get; } =
        new Dictionary<string, Type>
        {
            [PrepareCrossfade] = typeof(PrepareCrossfadeMessage),
            [PrepareTransition] = typeof(PrepareTransitionMessage),
        };
}
