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

using System.Text.Json;
using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.PluginSdk.Testing;

/// <summary>
/// A view as stable JSON, so a test asserts on what a client would draw.
/// <para>
/// Stable is the point. Property order follows the type rather than a
/// dictionary's iteration, and indentation is fixed, so a snapshot that
/// differs differs because the view changed and not because a hash seed did.
/// </para>
/// </summary>
public static class PluginViewSnapshot
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string Of(PluginView view) => JsonSerializer.Serialize(view, Options);
}
