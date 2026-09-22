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
/// How a page is opened, as values rather than as switches.
/// <para>
/// Every field here is something the host can read and apply itself. A command
/// line would have been a plugin passing unreviewed arguments to a browser the
/// platform is responsible for.
/// </para>
/// </summary>
public sealed record PluginBrowserOptions
{
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    public string? UserAgent { get; init; }

    public string? Referer { get; init; }

    public string Locale { get; init; } = "en";
}
