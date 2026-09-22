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
/// A port the owner consented to this plugin holding open.
/// <para>
/// The port is read back rather than assumed, because a plugin that asked for
/// any free port needs to know which one it got before it can announce itself
/// to a tracker or a peer.
/// </para>
/// </summary>
public interface IPluginListener : IAsyncDisposable
{
    /// <summary>The port actually bound, which is not the requested port when the request was zero.</summary>
    int Port { get; }

    /// <summary>The next inbound connection. Cancelling is how a plugin stops listening.</summary>
    Task<Stream> AcceptAsync(CancellationToken ct = default);
}
