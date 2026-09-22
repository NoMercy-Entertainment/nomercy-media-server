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

/// <summary>One recording the host owns.</summary>
public sealed record PluginRecording
{
    public required JobId Id { get; init; }
    public required string ChannelId { get; init; }
    public required DateTimeOffset Start { get; init; }
    public required DateTimeOffset Stop { get; init; }
    public required PluginRecordingState State { get; init; }
    public MediaId? Media { get; init; }
    public long Bytes { get; init; }
}
