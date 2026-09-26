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

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.PluginSdk.Verification;

/// <summary>
/// Whether marketplace trust refuses, or only says what it would refuse.
/// <para>
/// The first release that fetches keys, revocations and entitlements only
/// watches (config <c>Plugins:Trust:Enforce</c>, default false). A server
/// that has never checked anything must not start stopping plugins on the
/// day it first can; the owner reads the warnings first, and the next
/// release flips the default.
/// </para>
/// </summary>
public sealed class PluginTrustMode(bool enforce, ILogger logger)
{
    /// <summary>What a self-hosted owner greps for in the server log.</summary>
    public const string LogPrefix = "Plugin trust (warn-only): would refuse";

    /// <summary>The hard behaviour every gate had before the warn-only release.</summary>
    public static PluginTrustMode Enforcing { get; } = new(true, NullLogger.Instance);

    private readonly ConcurrentDictionary<string, byte> _logged = new();

    public bool Enforce => enforce;

    /// <summary>The refusal when enforcing; otherwise null, after one warning.</summary>
    public PluginRefusal? Apply(PluginRefusal? refusal)
    {
        if (refusal is null || enforce)
            return refusal;

        WouldRefuse(refusal.Plugin, refusal.Code, refusal.Why);

        return null;
    }

    /// <summary>
    /// Logged once per plugin and reason. A gate is asked every time a plugin
    /// starts, and a line repeated on every start is a line nobody reads.
    /// </summary>
    public void WouldRefuse(string plugin, string code, string reason)
    {
        if (!_logged.TryAdd($"{plugin}|{code}|{reason}", 0))
            return;

        logger.LogWarning(
            "{Prefix} {Plugin}: {Code}. {Reason} It runs anyway because Plugins:Trust:Enforce is off.",
            LogPrefix,
            plugin,
            code,
            reason
        );
    }
}
