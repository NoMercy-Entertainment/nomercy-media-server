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

/// <summary>Who is asking. No email and no token: a plugin needs neither to
/// greet someone or to key its own per-user storage.</summary>
public sealed record PluginUserIdentity
{
    public required UserId Id { get; init; }
    public required string DisplayName { get; init; }
    public bool IsOwner { get; init; }
    public string? Locale { get; init; }
}
