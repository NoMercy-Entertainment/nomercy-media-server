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

namespace NoMercy.Encoder.LiveTranscode;

/// <summary>
/// Told once when a live session leaves <see cref="ILiveStreamingService"/>,
/// whatever ended it: the client, the idle reaper, an eviction or shutdown.
/// </summary>
public interface ILiveSessionEndObserver
{
    Task OnSessionEndedAsync(string sessionId);
}
