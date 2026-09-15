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

namespace NoMercy.Networking.Connectivity;

public interface IConnectivityStrategy
{
    string Name { get; }
    int Priority { get; }
    Task<ConnectivityResult> TryEstablishAsync(CancellationToken ct);
    Task TeardownAsync();
    ConnectivityType Type { get; }

    /// <summary>
    /// Whether the transport this strategy established is still carrying traffic. Defaults
    /// to true for strategies that hold no process or socket of their own; a strategy that
    /// does owns the answer. Without it a transport could die minutes after being chosen and
    /// the server would keep advertising it for the rest of its uptime.
    /// </summary>
    bool IsStillEstablished => true;

    /// <summary>
    /// Whether this strategy's preconditions are met well enough to attempt it at all.
    /// Defaults to true for strategies with nothing to check first. This must stay
    /// synchronous and side-effect-free — a check that can itself fail for a network
    /// reason (a probe, an HTTP call) is not a precondition, it is another attempt wearing
    /// a different name, and collapses back into the same "not yet" vs "no" confusion this
    /// exists to remove. A strategy that is not ready is not evidence about the network;
    /// the caller must defer it rather than counting a skipped attempt as a failure.
    /// </summary>
    bool IsReady => true;

    /// <summary>
    /// Fired the instant the host begins shutting down — before Kestrel stops accepting
    /// requests and well before TeardownAsync runs. A strategy fronting a live process (the
    /// cloudflared tunnel) uses this to stop treating "origin unreachable" as a surprise
    /// during the window where the origin is closing on purpose. No-op for strategies with
    /// nothing to warn.
    /// </summary>
    void BeginShutdown() { }
}

/// <summary>
/// How much a strategy actually knows about the connectivity it reports. The distinction
/// exists because "I started" and "a client can reach us" are different claims, and
/// treating the first as the second is what let an unverifiable port forward outrank a
/// working tunnel.
/// </summary>
public enum ConnectivityConfidence
{
    None,

    /// <summary>
    /// The transport reported success, but nothing confirmed that a client outside the
    /// network can reach the published address. Usable as a last resort, never as a reason
    /// to stop looking for something better.
    /// </summary>
    Assumed,

    /// <summary>
    /// A connection over this transport actually completed.
    /// </summary>
    Verified,
}

public readonly record struct ConnectivityResult(
    bool Established,
    ConnectivityConfidence Confidence
)
{
    public static ConnectivityResult Failed() => new(false, ConnectivityConfidence.None);

    public static ConnectivityResult Verified() => new(true, ConnectivityConfidence.Verified);

    public static ConnectivityResult Assumed() => new(true, ConnectivityConfidence.Assumed);
}

public enum ConnectivityType
{
    DirectLan,
    PortForward,
    StunHolePunch,
    CloudflareTunnel,
    QuickTunnel,
    LocalOnly,
}

public enum ConnectivityState
{
    Starting,
    Evaluating,
    DirectAccess,
    HolePunched,
    Tunneled,
    LocalOnly,
}
