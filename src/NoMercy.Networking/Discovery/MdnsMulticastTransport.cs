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

using Makaretu.Dns;

namespace NoMercy.Networking.Discovery;

/// <summary>
/// The production transport: one <see cref="MulticastService"/> that joins
/// the mDNS group on <see cref="Start"/> and leaves it on <see cref="Stop"/>.
/// </summary>
public sealed class MdnsMulticastTransport : IMdnsMulticastTransport
{
    public MulticastService Service { get; } = new();

    public void Start() => Service.Start();

    public void Stop() => Service.Stop();

    public void Dispose() => Service.Dispose();
}
