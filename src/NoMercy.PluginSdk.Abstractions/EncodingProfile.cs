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

public class EncodingProfile
{
    public required string Name { get; init; }
    public required string VideoCodec { get; init; }
    public required string AudioCodec { get; init; }
    public string Container { get; init; } = "mp4";
    public int? Width { get; init; }
    public int? Height { get; init; }
    public int? VideoBitrate { get; init; }
    public int? AudioBitrate { get; init; }
    public Dictionary<string, string> ExtraParameters { get; init; } = new();
}
