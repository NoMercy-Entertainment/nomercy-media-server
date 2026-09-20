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

namespace NoMercy.Plugins.Abstractions;

/// <summary>
/// A media URL the host minted for one user and one session.
/// There is no constructor a plugin can reach, because the radio plugin kept the
/// viewer's bearer token in a mutable static and appended it to every stream URL.
/// </summary>
public sealed record PluginMediaUrl
{
    public Uri Url { get; internal init; } = null!;
    public DateTimeOffset ExpiresAt { get; internal init; }
    public MediaId Media { get; internal init; }
}
