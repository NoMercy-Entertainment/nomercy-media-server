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
/// Who is asking, right now.
/// <para>
/// A plugin's facades are built once per plugin, but the things they mint are
/// for one account. Asking at call time rather than holding a caller is the
/// difference between a link bound to whoever is listening and a link bound to
/// whoever happened to start the plugin.
/// </para>
/// <para>
/// Empty when nothing is asking: a scheduled job, a startup hook, a plugin
/// doing something on its own. Callers that need an account refuse rather than
/// picking one.
/// </para>
/// </summary>
public interface IPluginCallerAccessor
{
    Guid CurrentUserId { get; }
}

/// <summary>Nobody is asking, which is the honest answer outside a request.</summary>
public sealed class NoPluginCaller : IPluginCallerAccessor
{
    public static NoPluginCaller Instance { get; } = new();

    public Guid CurrentUserId => Guid.Empty;
}
