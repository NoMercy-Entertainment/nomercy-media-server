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
/// Publishing channels and a guide. The plugin knows the provider; the host
/// owns the storage, the ordering and what a client is allowed to see.
/// </summary>
public interface IPluginMediaLive
{
    Task PublishAsync(IReadOnlyList<PluginLiveChannel> channels, CancellationToken ct = default);

    Task PublishGuideAsync(IReadOnlyList<PluginEpgProgram> guide, CancellationToken ct = default);

    Task PublishGroupsAsync(
        IReadOnlyList<PluginChannelGroup> groups,
        CancellationToken ct = default
    );
}
