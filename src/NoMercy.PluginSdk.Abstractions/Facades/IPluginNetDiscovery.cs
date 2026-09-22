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
/// mDNS, SSDP and local peer discovery on the owner's network.
/// <para>
/// This is the most privacy-sensitive thing in the network facade: it enumerates
/// machines the owner never mentioned to the server. It is its own capability
/// for that reason, rather than riding along with dialling.
/// </para>
/// </summary>
public interface IPluginNetDiscovery
{
    /// <summary>
    /// Services answering on the local network, until cancelled. The stream
    /// never completes on its own; discovery is a running conversation, not a
    /// question with an answer.
    /// </summary>
    IAsyncEnumerable<PluginDiscoveredService> BrowseAsync(
        string protocol,
        CancellationToken ct = default
    );

    /// <summary>
    /// Announces this plugin's own service, so peers can find it the same way.
    /// <para>
    /// The announcement is live until the returned handle is disposed, which is
    /// what lets a plugin stop advertising without stopping. A bare task would
    /// have left it ambiguous whether announcing ended when the call returned.
    /// </para>
    /// </summary>
    Task<IAsyncDisposable> AnnounceAsync(
        string protocol,
        string instance,
        int port,
        IReadOnlyDictionary<string, string>? attributes = null,
        CancellationToken ct = default
    );
}

/// <param name="Protocol">The protocol it answered on, as the plugin asked for it.</param>
/// <param name="Attributes">The TXT record, or whatever the protocol calls its key-value bag.</param>
public sealed record PluginDiscoveredService(
    string Protocol,
    string Instance,
    string Host,
    int Port,
    IReadOnlyDictionary<string, string> Attributes
);
