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

namespace NoMercy.Encoder.Analysis;

public record AudioStreamInfo(
    int Index,
    string Codec,
    int Channels,
    int SampleRate,
    long BitRateKbps,
    string? Language,
    bool IsDefault,
    bool IsForced,
    double StartTimeSeconds = 0,
    double DelayVsVideoSeconds = 0,
    string? Profile = null,
    string? ChannelLayout = null
);
