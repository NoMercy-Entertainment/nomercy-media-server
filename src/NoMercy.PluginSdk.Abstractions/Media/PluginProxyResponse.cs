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
/// The upstream that answered, and the URL a client should play.
/// Which link won is reported because a provider that always falls through to
/// its last mirror is a provider that is failing quietly.
/// </summary>
public sealed record PluginProxyResponse
{
    public required PluginMediaUrl Url { get; init; }
    public required int LinkIndex { get; init; }
    public string? ContentType { get; init; }
}
