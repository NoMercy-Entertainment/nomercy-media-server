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

namespace NoMercy.Events.Playback;

public sealed class PlaybackToolsReadyEvent : EventBase, IEventExplainsItself
{
    public override string Source => "Binaries";

    public required int Attempt { get; init; }

    public string Why => $"Playback tools became ready on attempt {Attempt}";
}
