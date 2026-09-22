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
/// What is lined up after the current item.
/// Moving is its own call rather than a remove and an add, because the two
/// apart leave a window where the viewer's queue is briefly missing an entry.
/// </summary>
public interface IPluginPlayerQueue
{
    Task<IReadOnlyList<PluginPlaybackSource>> ListAsync(CancellationToken ct = default);

    Task AddAsync(
        PluginPlaybackSource source,
        int? position = null,
        CancellationToken ct = default
    );

    Task RemoveAsync(int position, CancellationToken ct = default);

    Task MoveAsync(int from, int to, CancellationToken ct = default);
}
