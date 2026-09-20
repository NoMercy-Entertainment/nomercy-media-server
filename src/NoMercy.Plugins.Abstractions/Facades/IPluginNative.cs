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

namespace NoMercy.Plugins.Abstractions;

/// <summary>
/// Native libraries a plugin ships with itself.
/// <para>
/// A native library is machine code the host cannot inspect, sandbox or refuse
/// once it is running, so this is the one facade whose gate is the marketplace
/// signature rather than a capability the owner ticks. An unsigned bundle
/// refuses with <see cref="PluginRefusalCodes.NativeCodeUnsigned" />, however
/// many capabilities the manifest declares.
/// </para>
/// </summary>
public interface IPluginNative
{
    /// <summary>
    /// Makes <paramref name="library" /> resolvable to this plugin's own load
    /// context, so its <c>DllImport</c> declarations find it. The name is the
    /// library's, without a platform prefix or file extension; the host picks
    /// the file for the platform it is on.
    /// <para>
    /// A bare name, never a path. A separator or a <c>..</c> segment refuses
    /// with <see cref="PluginRefusalCodes.FileOutsideGrant" /> rather than
    /// resolving: the signature says the bundle was built by the marketplace,
    /// and it says nothing about a file reached from outside that bundle.
    /// </para>
    /// </summary>
    Task LoadAsync(string library, CancellationToken ct = default);

    bool IsLoaded(string library);
}
