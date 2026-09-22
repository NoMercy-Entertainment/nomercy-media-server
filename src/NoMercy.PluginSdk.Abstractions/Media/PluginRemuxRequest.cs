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
/// Repackage a stream without re-encoding it.
/// </summary>
public sealed record PluginRemuxRequest
{
    public required Uri Source { get; init; }
    public required PluginRemuxContainer Container { get; init; }
    public string? Referer { get; init; }
    public string? UserAgent { get; init; }
}
