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

using System.Net;
using NoMercy.Networking.Discovery;
using NoMercy.NmSystem.Configuration;
using NoMercy.NmSystem.Information;
using NoMercy.Setup.Boot;

namespace NoMercy.Setup.Ui;

/// <summary>
/// The addresses a first-run owner can open the setup page on, and the lines every
/// console and log site prints for them (issue #437). The LAN address comes first,
/// so an owner who runs the server on a NAS can paste it on a laptop; the localhost
/// line follows. With no LAN address only the localhost line is printed. Inside a
/// container that only knows its own bridge address, a line tells the owner to use
/// the NAS address and the host port they mapped.
/// </summary>
public sealed class SetupAddress
{
    private const string NetworkLabel = "Open on any device on your network: ";
    private const string LocalhostLabel = "On this machine: ";
    private const string ContainerPortHint =
        "If you mapped a different host port, use your NAS address and that port.";

    private SetupAddress(string? networkUrl, string localhostUrl, bool containerPortHint)
    {
        NetworkUrl = networkUrl;
        LocalhostUrl = localhostUrl;
        List<string> lines = [];
        if (networkUrl is not null)
            lines.Add(NetworkLabel + networkUrl);
        lines.Add(LocalhostLabel + localhostUrl);
        if (containerPortHint)
            lines.Add(ContainerPortHint);
        Lines = lines;
    }

    /// <summary>The setup page on the LAN address, or null when no LAN address is known.</summary>
    public string? NetworkUrl { get; }

    /// <summary>The setup page on localhost; always present.</summary>
    public string LocalhostUrl { get; }

    /// <summary>The one URL to show where only one fits: the LAN URL when known, else localhost.</summary>
    public string PreferredUrl => NetworkUrl ?? LocalhostUrl;

    /// <summary>The lines to print, in order: network, localhost, container hint.</summary>
    public IReadOnlyList<string> Lines { get; }

    /// <summary>
    /// Build from the address the server registers with (<see cref="INetworkDiscovery.InternalIp"/>,
    /// which honors <c>--internal-ip</c> / <c>NOMERCY_INTERNAL_IP</c>), the internal port,
    /// and container detection.
    /// </summary>
    public static SetupAddress Current()
    {
        return Resolve(
            Start.NetworkDiscovery?.InternalIp,
            RuntimeServerSettings.Current.InternalServerPort,
            Screen.IsDocker
        );
    }

    /// <summary>
    /// Pure builder. A LAN address is "known" when it is set and is neither the loopback
    /// nor the unspecified address. The container hint is added when running in a
    /// container and the only known address is loopback or a Docker/WSL bridge address,
    /// which is what <see cref="NetworkDiscovery"/> resolves when no host IP was supplied.
    /// </summary>
    public static SetupAddress Resolve(string? lanIp, int port, bool inContainer)
    {
        string? networkUrl = IsLanAddress(lanIp) ? $"http://{lanIp}:{port}/setup" : null;
        string localhostUrl = $"http://localhost:{port}/setup";
        bool containerPortHint = inContainer && (networkUrl is null || IsBridgeAddress(lanIp!));
        return new SetupAddress(networkUrl, localhostUrl, containerPortHint);
    }

    private static bool IsLanAddress(string? ip)
    {
        return !string.IsNullOrEmpty(ip) && ip != "127.0.0.1" && ip != "0.0.0.0";
    }

    private static bool IsBridgeAddress(string ip)
    {
        return IPAddress.TryParse(ip, out IPAddress? parsed)
            && NetworkDiscovery.IsDockerOrWslAddress(parsed);
    }
}
