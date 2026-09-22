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

using System.Diagnostics;
using NoMercy.PluginSdk.Ipc;

namespace NoMercy.PluginHost;

/// <summary>What this process can say about itself, without asking the plugin.</summary>
public static class PluginHostHealth
{
    private static readonly DateTimeOffset Started = DateTimeOffset.UtcNow;

    public static int Restarts { get; set; }

    public static string? LastRefusalCode { get; set; }

    public static PluginHealthSnapshot Read()
    {
        Process self = Process.GetCurrentProcess();

        return new(
            self.Id,
            DateTimeOffset.UtcNow - Started,
            0,
            self.WorkingSet64,
            0,
            Restarts,
            LastRefusalCode
        );
    }
}

/// <summary>
/// Stopping, asked for from the outside.
/// <para>
/// The server says shut down and this process ends itself, so the plugin's
/// own Dispose runs. Killed instead, a plugin holding a half-written file
/// leaves it half written.
/// </para>
/// </summary>
public static class PluginHostLifetime
{
    private static readonly CancellationTokenSource Source = new();

    public static CancellationToken Stopping => Source.Token;

    public static void RequestStop()
    {
        if (!Source.IsCancellationRequested)
            Source.Cancel();
    }
}

/// <summary>
/// The quotas this process puts on itself, from the launch environment.
/// Filled per platform in the sandbox tasks; a no-op until then, so a host
/// that reads the variables today behaves exactly as it does without them.
/// </summary>
public static class SelfLimits
{
    public static void Apply(IReadOnlyDictionary<string, string?> environment)
    {
        _ = environment;
    }
}
